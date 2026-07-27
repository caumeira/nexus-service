using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS IProcessIconProvider: NSWorkspace iconForFile: on the process's
/// owning .app bundle, rendered to a square PNG via ImageIO. NSWorkspace
/// covers every icon source (Assets.car-only bundles, .icns, generic
/// executables), which a direct Info.plist/.icns reader would not. All
/// AppKit object use is confined to one dedicated worker thread, the same
/// isolation shape as WindowsIconExtractor's STA thread; callers marshal in
/// through a queue and time out instead of wedging a route thread.
/// </summary>
public sealed class MacProcessIconProvider : IProcessIconProvider, IDisposable
{
    // Proposed size in points; AppKit returns the nearest source rep at or
    // above it (a Retina 2x rep for most bundles), it is not an output cap.
    private const int IconSizePts = 256;
    private const int RequestTimeoutMs = 5000;

    private readonly Thread _worker;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());

    public MacProcessIconProvider()
    {
        _worker = new Thread(RunWorkerLoop)
        {
            IsBackground = true,
            Name = "NexusIconExtractAppKit",
        };
        _worker.Start();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker.Join(1000); } catch { }
        _queue.Dispose();
    }

    /// <summary>Null only when the worker could not answer in time (transient,
    /// not cached by the route); empty bytes when extraction ran and found no
    /// icon.</summary>
    public byte[]? GetIcon(string exePath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return Array.Empty<byte>();
        }

        // TaskCompletionSource instead of a ManualResetEventSlim: a timed-out
        // caller returns while the queued closure still holds the completion
        // object, and a TCS has no disposal to race against that late Set.
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { tcs.TrySetResult(ExtractPng(ResolveIconTargetPath(exePath))); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[mac-icon] extraction failed: {ex.Message}");
                    tcs.TrySetResult(Array.Empty<byte>());
                }
            });
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<byte>();
        }
        if (!tcs.Task.Wait(RequestTimeoutMs))
        {
            Console.Error.WriteLine("[mac-icon] extraction timed out");
            return null;
        }
        return tcs.Task.Result;
    }

    /// <summary>Outermost .app ancestor of the exe path (helper-bundle
    /// processes like browser renderers then surface the product's icon,
    /// not a generic executable); the exe path itself when no bundle owns
    /// it.</summary>
    internal static string ResolveIconTargetPath(string exePath)
    {
        var segments = exePath.Split('/');
        var prefixLength = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            prefixLength += segments[i].Length + (i > 0 ? 1 : 0);
            if (segments[i].Length > ".app".Length &&
                segments[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return exePath[..prefixLength];
            }
        }
        return exePath;
    }

    private void RunWorkerLoop()
    {
        // Idempotent; guarantees AppKit is initialized even when the status
        // bar (the other AppKit consumer) is not running, e.g. NEXUS_TEST_HOST.
        try { NSApplicationLoad(); } catch { }
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Console.Error.WriteLine($"[mac-icon] worker exception: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException) { /* queue completed */ }
    }

    private static byte[] ExtractPng(string iconTargetPath)
    {
        // CGImageForProposedRect returns an autoreleased CGImage; the pool
        // drain below is what releases it, so it must outlive EncodePng.
        var pool = MsgSend(MsgSend(ClassGet("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        var cfPath = IntPtr.Zero;
        try
        {
            cfPath = CFStringCreateWithCString(IntPtr.Zero, iconTargetPath, KCfStringEncodingUtf8);
            if (cfPath == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            var workspace = MsgSend(ClassGet("NSWorkspace"), Sel("sharedWorkspace"));
            // CFString is toll-free bridged to the NSString iconForFile: expects.
            var icon = MsgSend(workspace, Sel("iconForFile:"), cfPath);
            if (icon == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            var rect = new NSRect { X = 0, Y = 0, Width = IconSizePts, Height = IconSizePts };
            var cgImage = MsgSend_RectPtr(icon, Sel("CGImageForProposedRect:context:hints:"), ref rect, IntPtr.Zero, IntPtr.Zero);
            return cgImage == IntPtr.Zero ? Array.Empty<byte>() : EncodePng(cgImage);
        }
        finally
        {
            if (cfPath != IntPtr.Zero)
            {
                CFRelease(cfPath);
            }
            MsgSend(pool, Sel("drain"));
        }
    }

    private static byte[] EncodePng(IntPtr cgImage)
    {
        var data = IntPtr.Zero;
        var type = IntPtr.Zero;
        var dest = IntPtr.Zero;
        try
        {
            data = CFDataCreateMutable(IntPtr.Zero, 0);
            type = CFStringCreateWithCString(IntPtr.Zero, "public.png", KCfStringEncodingUtf8);
            if (data == IntPtr.Zero || type == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            dest = CGImageDestinationCreateWithData(data, type, 1, IntPtr.Zero);
            if (dest == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            CGImageDestinationAddImage(dest, cgImage, IntPtr.Zero);
            if (!CGImageDestinationFinalize(dest))
            {
                return Array.Empty<byte>();
            }

            var length = (int)CFDataGetLength(data);
            if (length <= 0)
            {
                return Array.Empty<byte>();
            }
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return bytes;
        }
        finally
        {
            if (dest != IntPtr.Zero)
            {
                CFRelease(dest);
            }
            if (type != IntPtr.Zero)
            {
                CFRelease(type);
            }
            if (data != IntPtr.Zero)
            {
                CFRelease(data);
            }
        }
    }

    // ── Objective-C runtime / CoreFoundation / ImageIO P/Invoke ──────────────

    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string Appkit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIo = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

    private const uint KCfStringEncodingUtf8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGet([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel, IntPtr arg1);

    // CGImageForProposedRect:context:hints: - (NSRect*, NSGraphicsContext*, NSDictionary*)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_RectPtr(IntPtr receiver, IntPtr sel, ref NSRect rect, IntPtr context, IntPtr hints);

    [DllImport(Appkit, EntryPoint = "NSApplicationLoad")]
    private static extern byte NSApplicationLoad();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPStr)] string str, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr obj);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreateMutable(IntPtr allocator, long capacity);

    [DllImport(CoreFoundation)]
    private static extern long CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(ImageIo)]
    private static extern IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, long count, IntPtr options);

    [DllImport(ImageIo)]
    private static extern void CGImageDestinationAddImage(IntPtr dest, IntPtr image, IntPtr properties);

    [DllImport(ImageIo)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGImageDestinationFinalize(IntPtr dest);
}
