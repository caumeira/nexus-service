using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qos.Service.Platform.Mac;

/// <summary>
/// NSWindow + WKWebView host - the macOS native shell. Replaces the
/// previous `Chrome / Edge --app=URL` launch path so the .app bundle is
/// self-sufficient: WKWebView is part of macOS, so we never have to ask the
/// user to install Chrome.
///
/// Uses the standard macOS titled window so the system draws its native
/// title bar with the "Qos" caption, traffic-light controls
/// (close / minimize / maximize) at the top-left, and the standard drag /
/// resize affordances. WKWebView fills the content view below.
///
/// AppKit calls are dispatched onto the main thread via the same
/// performSelectorOnMainThread bridge MacStatusBar uses. The class registers
/// its own NSObject subclass for menu / window / navigation callbacks.
/// </summary>
internal static class MacAppWindow
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static IntPtr _window;
    private static IntPtr _webView;
    private static IntPtr _targetObj;
    private static bool _classRegistered;
    private static string _pendingUrl = "about:blank";
    private static bool _pendingNavigate = true;
    private static readonly object _sync = new();
    // Guards the one-time class registration so concurrent callers wait
    // until _targetObj is fully assigned before any of them dispatches
    // openOrFocus: against it. An Interlocked.CompareExchange race here
    // would let the second caller see "registered" and proceed with
    // _targetObj == IntPtr.Zero.
    private static readonly object _classRegistrationSync = new();

    /// <summary>
    /// Open the dashboard window, or focus the existing one and navigate to
    /// the given URL. Safe to call from any thread - dispatches onto main.
    /// </summary>
    /// <param name="navigateIfOpen">
    /// When the window is already open, true reissues loadRequest: (the
    /// menu-bar "Open Dashboard" / "Settings" path) and false just brings the
    /// window forward without reloading (the kAEReopenApplication / Dock
    /// re-click path - clicking the Dock icon must not refresh the page).
    /// New windows always navigate to the URL regardless of this flag.
    /// </param>
    public static void OpenOrFocus(string url, bool navigateIfOpen = true)
    {
        if (!IsSupported) return;
        if (string.IsNullOrWhiteSpace(url)) return;

        lock (_sync) { _pendingUrl = url; _pendingNavigate = navigateIfOpen; }

        try
        {
            EnsureClassRegistered();

            // Schedule the actual window work on the AppKit main thread.
            IntPtr selPerform = SelRegister("performSelectorOnMainThread:withObject:waitUntilDone:");
            IntPtr selOpenOrFocus = SelRegister("openOrFocus:");
            MsgSend_Perform(_targetObj, selPerform, selOpenOrFocus, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-app-window] OpenOrFocus failed: {ex.Message}");
        }
    }

    private static unsafe void EnsureClassRegistered()
    {
        lock (_classRegistrationSync)
        {
            if (_classRegistered) return;

            // Force WebKit framework to load via dlopen so
            // objc_getClass("WKWebView") resolves. WebKit's ObjC classes
            // don't auto-register until the framework is mapped in.
            const int RTLD_NOW = 2;
            var handle = dlopen("/System/Library/Frameworks/WebKit.framework/WebKit", RTLD_NOW);
            if (handle == IntPtr.Zero)
            {
                Console.Error.WriteLine("[mac-app-window] dlopen WebKit failed");
            }

            IntPtr nsObject = ClassGet("NSObject");
            IntPtr targetClass = objc_allocateClassPair(nsObject, "QosAppWindowTarget", IntPtr.Zero);
            if (targetClass == IntPtr.Zero)
            {
                // A previous registration attempt already created the class.
                targetClass = ClassGet("QosAppWindowTarget");
                if (targetClass == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to allocate QosAppWindowTarget");
            }
            else
            {
                AddMethod(targetClass, "openOrFocus:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OpenOrFocusImpl, "v@:@");
                AddMethod(targetClass, "windowWillClose:", (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&WindowWillCloseImpl, "v@:@");
                objc_registerClassPair(targetClass);
            }

            IntPtr selAlloc = SelRegister("alloc");
            IntPtr selInit = SelRegister("init");
            _targetObj = MsgSend(MsgSend(targetClass, selAlloc), selInit);
            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OpenOrFocusImpl(IntPtr self, IntPtr cmd, IntPtr arg)
    {
        try
        {
            string url;
            bool navigate;
            lock (_sync) { url = _pendingUrl; navigate = _pendingNavigate; }

            // A freshly-created window always needs an initial loadRequest:,
            // otherwise the WKWebView shows about:blank. Skip the reload only
            // when the window already existed and the caller asked us to.
            bool freshlyCreated = _window == IntPtr.Zero;
            if (freshlyCreated)
            {
                CreateWindowOnMain();
            }
            if (freshlyCreated || navigate)
            {
                NavigateToOnMain(url);
            }
            BringToFrontOnMain();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-app-window] OpenOrFocusImpl failed: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void WindowWillCloseImpl(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        // User clicked the close button. Drop the window references so the
        // next OpenOrFocus rebuilds a fresh window instead of trying to
        // resurrect a closed one. The service stays running because
        // setReleasedWhenClosed:NO + the menu-bar agent.
        try
        {
            Console.WriteLine("[mac-app-window] window will close");
            _window = IntPtr.Zero;
            _webView = IntPtr.Zero;
            // Drop back to Accessory so the Dock icon disappears - we are
            // back to "menu bar agent only" until the user re-opens the
            // dashboard.
            RestoreAccessoryPolicyOnMain();
        }
        catch { }
    }

    private static void CreateWindowOnMain()
    {
        IntPtr classNSWindow = ClassGet("NSWindow");
        IntPtr classWKWebView = ClassGet("WKWebView");
        IntPtr classWKConfig = ClassGet("WKWebViewConfiguration");
        if (classNSWindow == IntPtr.Zero || classWKWebView == IntPtr.Zero || classWKConfig == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[mac-app-window] missing class: NSWindow={classNSWindow:x} WKWebView={classWKWebView:x} WKConfig={classWKConfig:x}");
            return;
        }

        IntPtr selAlloc = SelRegister("alloc");
        IntPtr selInit = SelRegister("init");

        // ── NSWindow ────────────────────────────────────────────────────────
        // Standard titled window. The system draws the title bar (caption
        // "Qos", traffic-light controls at top-left); the WKWebView
        // sits in the content area below.
        const ulong NSWindowStyleMaskTitled = 1UL << 0;
        const ulong NSWindowStyleMaskClosable = 1UL << 1;
        const ulong NSWindowStyleMaskMiniaturizable = 1UL << 2;
        const ulong NSWindowStyleMaskResizable = 1UL << 3;
        const ulong styleMask = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
            | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable;
        const ulong NSBackingStoreBuffered = 2;

        var contentRect = new NSRect { x = 200, y = 200, width = 1280, height = 820 };

        IntPtr win = MsgSend(classNSWindow, selAlloc);
        win = MsgSend_InitWindow(
            win,
            SelRegister("initWithContentRect:styleMask:backing:defer:"),
            contentRect, styleMask, NSBackingStoreBuffered, false);

        MsgSend(win, SelRegister("setTitle:"), NsString("Qos"));
        // Default setReleasedWhenClosed:YES is what we want - when the user
        // closes the window via the red traffic light, AppKit deallocates
        // the NSWindow (and its content view chain, including the
        // WKWebView), freeing the WebContent renderer process. Our
        // windowWillClose: delegate nils _window before that happens so
        // the next OpenOrFocus rebuilds fresh.
        MsgSend(win, SelRegister("setDelegate:"), _targetObj);

        // ── WKWebView ───────────────────────────────────────────────────────
        IntPtr config = MsgSend(MsgSend(classWKConfig, selAlloc), selInit);

        // Initial frame matches the window content area; autoresizing keeps
        // it filling on resize.
        var webFrame = new NSRect { x = 0, y = 0, width = contentRect.width, height = contentRect.height };
        IntPtr webView = MsgSend(classWKWebView, selAlloc);
        webView = MsgSend_InitWebView(
            webView,
            SelRegister("initWithFrame:configuration:"),
            webFrame, config);

        // NSViewWidthSizable (2) | NSViewHeightSizable (16) = 18
        MsgSendVoidLong(webView, SelRegister("setAutoresizingMask:"), 18);

        // setContentView:webView - WKWebView fills the content area below
        // the standard title bar.
        MsgSend(win, SelRegister("setContentView:"), webView);

        // Drop our +1 retains from alloc/init. NSWindow's contentView
        // property has retained the WKWebView, and the WKWebView holds
        // its WKWebViewConfiguration; without these releases the refcount
        // never reaches zero on close, leaking ~3 Cocoa objects per
        // open/close cycle (WKWebView's WebContent process is the heavy
        // one, ~150 MB).
        IntPtr selRelease = SelRegister("release");
        MsgSend(webView, selRelease);
        MsgSend(config, selRelease);

        // Center on screen.
        MsgSend(win, SelRegister("center"));

        _window = win;
        _webView = webView;
    }

    private static void NavigateToOnMain(string url)
    {
        if (_webView == IntPtr.Zero) return;

        IntPtr classNSURL = ClassGet("NSURL");
        IntPtr classNSURLRequest = ClassGet("NSURLRequest");

        IntPtr nsUrlString = NsString(url);
        IntPtr nsUrl = MsgSend(classNSURL, SelRegister("URLWithString:"), nsUrlString);
        if (nsUrl == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[mac-app-window] NSURL URLWithString failed: {url}");
            return;
        }

        IntPtr nsRequest = MsgSend(classNSURLRequest, SelRegister("requestWithURL:"), nsUrl);
        if (nsRequest == IntPtr.Zero) return;

        MsgSend(_webView, SelRegister("loadRequest:"), nsRequest);
    }

    private static void BringToFrontOnMain()
    {
        if (_window == IntPtr.Zero) return;

        IntPtr classNSApp = ClassGet("NSApplication");
        IntPtr nsApp = MsgSend(classNSApp, SelRegister("sharedApplication"));

        // Switch to Regular activation policy so the window can become
        // frontmost. LSUIElement / Accessory mode hides the Dock icon but
        // also prevents the agent from stealing focus, so a freshly-opened
        // NSWindow stays buried behind the user's current app. We bump up
        // to Regular (0) here, activate, then drop back to Accessory (1)
        // when the window closes (windowWillClose:) so the Dock icon
        // disappears again - a Slack/Discord-style "menu bar agent that
        // also has a real window" pattern.
        const long NSApplicationActivationPolicyRegular = 0;
        MsgSendLong_ret_bool(nsApp, SelRegister("setActivationPolicy:"), NSApplicationActivationPolicyRegular);

        MsgSendVoidBool(nsApp, SelRegister("activateIgnoringOtherApps:"), true);
        MsgSend(_window, SelRegister("makeKeyAndOrderFront:"), IntPtr.Zero);
    }

    private static void RestoreAccessoryPolicyOnMain()
    {
        try
        {
            IntPtr classNSApp = ClassGet("NSApplication");
            IntPtr nsApp = MsgSend(classNSApp, SelRegister("sharedApplication"));
            const long NSApplicationActivationPolicyAccessory = 1;
            MsgSendLong_ret_bool(nsApp, SelRegister("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);
        }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IntPtr NsString(string s)
    {
        IntPtr classNSString = ClassGet("NSString");
        IntPtr sel = SelRegister("stringWithUTF8String:");
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s + '\0');
        unsafe
        {
            fixed (byte* p = utf8)
            {
                return MsgSend(classNSString, sel, (IntPtr)p);
            }
        }
    }

    private static void AddMethod(IntPtr cls, string selectorName, IntPtr impPtr, string typeEncoding)
    {
        IntPtr sel = SelRegister(selectorName);
        if (!class_addMethod(cls, sel, impPtr, typeEncoding))
        {
            throw new InvalidOperationException($"class_addMethod failed for {selectorName}");
        }
    }

    private static IntPtr SelRegister(string name) => SelRegisterPInvoke(name);
    private static IntPtr ClassGet(string name) => ClassGetPInvoke(name);

    // ── P/Invoke ─────────────────────────────────────────────────────────────

    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string WebKit = "/System/Library/Frameworks/WebKit.framework/WebKit";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double x, y, width, height; }

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterPInvoke([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGetPInvoke([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr extraBytes);

    [DllImport(Libobjc, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Libobjc, EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(AppKit, EntryPoint = "NSApplicationLoad")]
    private static extern byte NSApplicationLoad();

    [DllImport("libSystem.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string path, int mode);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidBool(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidLong(IntPtr receiver, IntPtr sel, long arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendLong_ret_bool(IntPtr receiver, IntPtr sel, long arg1);

    // initWithContentRect:styleMask:backing:defer: -> (NSRect, NSUInteger, NSUInteger, BOOL)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitWindow(IntPtr receiver, IntPtr sel, NSRect rect, ulong styleMask, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);

    // initWithFrame:configuration: -> (NSRect, id)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_InitWebView(IntPtr receiver, IntPtr sel, NSRect frame, IntPtr config);

    // performSelectorOnMainThread:withObject:waitUntilDone:
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Perform(IntPtr receiver, IntPtr sel, IntPtr selArg, IntPtr withObject, [MarshalAs(UnmanagedType.I1)] bool waitUntilDone);
}
