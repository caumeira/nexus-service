using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Native banner via UNUserNotificationCenter, attributed to the Nexus bundle
/// (com.hellonexus.panel.service) so it actually slides out - osascript
/// notifications are attributed to Script Editor, which only lands them in the
/// list with no banner. The macOS analog of the Windows tray balloon.
///
/// Same objc_msgSend P/Invoke bridge as <see cref="MacStatusBar"/>; runs in the
/// main Nexus process (a shared NSApplication + run loop is already up), so the
/// banner carries the Nexus identity + icon. UNUserNotificationCenter is
/// thread-safe, so the background TransferNeedsAttention thread calls directly.
/// </summary>
internal static class MacNotify
{
    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string UserNotifications = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    // UNAuthorizationOptions: Badge=1, Sound=2, Alert=4.
    private const nuint AuthOptions = 1 | 2 | 4;

    private static bool _frameworkLoaded;

    /// <summary>
    /// Ask once at startup so the permission prompt isn't tied to the first
    /// transfer. Granted state persists in System Settings → Notifications.
    /// </summary>
    internal static void RequestAuthorization()
    {
        try
        {
            var center = Center();
            if (center == IntPtr.Zero)
                return;
            MsgSend_AuthReq(center, Sel("requestAuthorizationWithOptions:completionHandler:"),
                AuthOptions, _noopBlock);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[mac-notify] requestAuthorization failed: {ex.Message}");
        }
    }

    internal static void Send(string title, string body)
    {
        var center = Center();
        if (center == IntPtr.Zero)
            return;

        // Fires on a threadpool thread (TransferNeedsAttention) with no ambient
        // autorelease pool; NsString/requestWithIdentifier: return autoreleased
        // temporaries that would otherwise leak until process exit.
        var pool = objc_autoreleasePoolPush();
        try
        {
            var content = MsgSend(MsgSend(ClassGet("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            if (content == IntPtr.Zero)
                return;
            MsgSend(content, Sel("setTitle:"), NsString(title));
            MsgSend(content, Sel("setBody:"), NsString(body));

            // trigger nil ⇒ deliver now; identifier unique so notices stack
            // instead of replacing each other.
            var request = MsgSend(ClassGet("UNNotificationRequest"),
                Sel("requestWithIdentifier:content:trigger:"),
                NsString(Guid.NewGuid().ToString("N")), content, IntPtr.Zero);

            MsgSend_Add(center, Sel("addNotificationRequest:withCompletionHandler:"), request, _noopBlock);

            // content was +1 from alloc/init; the request copies it.
            MsgSend(content, Sel("release"));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[mac-notify] send failed: {ex.Message}");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr Center()
    {
        if (!_frameworkLoaded)
        {
            // UserNotifications' classes aren't auto-registered; dlopen first
            // (same as MacAppWindow does for WebKit).
            if (dlopen(UserNotifications, 0x2 /* RTLD_NOW */) == IntPtr.Zero)
            {
                ServiceLog.Warn("[mac-notify] UserNotifications.framework failed to load");
                return IntPtr.Zero;
            }
            _frameworkLoaded = true;
        }
        var cls = ClassGet("UNUserNotificationCenter");
        return cls == IntPtr.Zero ? IntPtr.Zero : MsgSend(cls, Sel("currentNotificationCenter"));
    }

    private static IntPtr NsString(string s)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        unsafe
        {
            fixed (byte* p = utf8)
            {
                return MsgSend(ClassGet("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
            }
        }
    }

    // ── A single reusable no-op completion block ─────────────────────────────
    // requestAuthorization/addNotificationRequest each take a block; we ignore
    // the result. One global no-op block serves both - its invoke reads only
    // the block ptr and the extra register args (BOOL/NSError*) are harmless on
    // the ARM64/x64 calling convention.

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    // Built once by the type initializer (CLR-guaranteed single-threaded), so
    // the background and startup callers never race a torn read. Process-
    // lifetime; never freed. IntPtr.Zero if dlsym failed ⇒ a nil completion
    // handler, which both APIs accept.
    private static readonly IntPtr _noopBlock = BuildNoopBlock();

    private static unsafe IntPtr BuildNoopBlock()
    {
        // _NSConcreteGlobalBlock is the isa for a capture-free static block.
        var isa = dlsym(dlopen(null, 0x2), "_NSConcreteGlobalBlock");
        if (isa == IntPtr.Zero)
            return IntPtr.Zero;

        var descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
        Marshal.StructureToPtr(
            new BlockDescriptor { Reserved = 0, Size = (nuint)Marshal.SizeOf<BlockLiteral>() }, descPtr, false);

        var block = new BlockLiteral
        {
            Isa = isa,
            Flags = 1 << 28, // BLOCK_IS_GLOBAL
            Reserved = 0,
            Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&NoopInvoke,
            Descriptor = descPtr,
        };
        var blockPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        Marshal.StructureToPtr(block, blockPtr, false);
        return blockPtr;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NoopInvoke(IntPtr block) { }

    // ── objc runtime / dyld P/Invoke ─────────────────────────────────────────

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGet([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    // requestAuthorizationWithOptions:completionHandler: - (NSUInteger, block)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_AuthReq(IntPtr receiver, IntPtr sel, nuint options, IntPtr block);

    // addNotificationRequest:withCompletionHandler: - (id, block)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Add(IntPtr receiver, IntPtr sel, IntPtr request, IntPtr block);

    [DllImport(Libobjc, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Libobjc, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string? path, int mode);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);
}
