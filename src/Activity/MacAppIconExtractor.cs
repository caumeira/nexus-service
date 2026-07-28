using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Activity;

/// <summary>
/// Shared macOS icon extraction: NSWorkspace iconForFile: rendered to a
/// square PNG via ImageIO, for any file or .app bundle path. NSWorkspace
/// covers every icon source (Assets.car-only bundles, .icns, generic
/// executables), which a direct Info.plist/.icns reader would not. All
/// AppKit object use is confined to one dedicated worker thread, the same
/// isolation shape as WindowsIconExtractor's STA thread; callers marshal in
/// through a queue and time out instead of wedging a route thread. Consumed
/// by MacProcessIconProvider (monitoring process icons) and
/// MacShortcutsProvider (deck / app-launch icons).
/// </summary>
public sealed class MacAppIconExtractor : IDisposable
{
    // Proposed size in points; AppKit returns the nearest source rep at or
    // above it (a Retina 2x rep for most bundles), it is not an output cap.
    public const int DefaultIconSizePts = 256;
    private const int RequestTimeoutMs = 5000;

    private readonly object _startLock = new();
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private Thread? _worker;

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker?.Join(1000); } catch { }
        _queue.Dispose();
    }

    // The worker starts on the first extraction, never in the ctor: the DI
    // graph constructs this during host startup, and a worker thread touching
    // AppKit there races MacStatusBar's main-thread AppKit init - the race
    // wedges the status item and the app exits silently minutes later.
    private void EnsureWorkerStarted()
    {
        if (_worker is not null)
        {
            return;
        }
        lock (_startLock)
        {
            if (_worker is not null)
            {
                return;
            }
            var worker = new Thread(RunWorkerLoop)
            {
                IsBackground = true,
                Name = "NexusIconExtractAppKit",
            };
            worker.Start();
            _worker = worker;
        }
    }

    /// <summary>Null only when the worker could not answer in time (transient,
    /// callers must not cache it as a negative); empty bytes when extraction
    /// ran and found no icon.</summary>
    public byte[]? ExtractPng(string fileOrBundlePath, int sizePts = DefaultIconSizePts)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(fileOrBundlePath))
        {
            return Array.Empty<byte>();
        }

        EnsureWorkerStarted();

        // TaskCompletionSource instead of a ManualResetEventSlim: a timed-out
        // caller returns while the queued closure still holds the completion
        // object, and a TCS has no disposal to race against that late Set.
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { tcs.TrySetResult(ExtractPngOnWorker(fileOrBundlePath, sizePts)); }
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

    private void RunWorkerLoop()
    {
        // Plain dylib load so NSWorkspace resolves in a headless process
        // (NEXUS_TEST_HOST); in the bundled app MacStatusBar already loaded
        // AppKit, making this a no-op refcount. Never NSApplicationLoad off
        // the main thread - see the EnsureWorkerStarted note.
        try { NativeLibrary.Load(Appkit); } catch { }
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

    private static byte[] ExtractPngOnWorker(string iconTargetPath, int sizePts)
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

            var rect = new NSRect { X = 0, Y = 0, Width = sizePts, Height = sizePts };
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
