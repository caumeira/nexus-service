#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// <see cref="ITryxPanoramaTransport"/> for the RK-firmware Panorama (VID 0x391A),
/// which binds to the Windows usbprint device class rather than CDC serial. Opened
/// with CreateFileW against the usbprint device interface path.
/// The panel replies on its IN endpoint after writes; if the host never reads that
/// endpoint the pipe backs up and the usbprint stack resets the interface, killing
/// the write handle every few tens of seconds (observed: a write-only loop dies at
/// 20-70s, a loop that also drains the reads survives indefinitely). So the handle
/// is opened overlapped and a background loop continuously reads and discards the IN
/// endpoint to keep the pipe alive. Those bytes carry nothing the caller needs.
/// The handle is a <see cref="SafeFileHandle"/> owned by the <see cref="FileStream"/>
/// so an in-flight write can't race a concurrent Dispose onto a recycled handle.
/// </summary>
public sealed class WindowsTryxPrinterTransport : ITryxPanoramaTransport
{
    private readonly FileStream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _drainCts = new();
    private readonly Task _drainTask;
    private bool _disposed;
    private volatile IReadOnlyList<string> _availableMediaIds = Array.Empty<string>();

    public WindowsTryxPrinterTransport(string devicePath, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Serial = serial ?? "";
        PortName = devicePath;
        var handle = Native.CreateFileW(
            devicePath,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }
        // isAsync matches FILE_FLAG_OVERLAPPED so a concurrent write and the background
        // read complete as overlapped I/O. The stream owns and frees the handle.
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: true);
        _drainTask = Task.Run(() => DrainReadsAsync(_drainCts.Token));
    }

    public bool IsOpen => !_disposed && _stream.SafeFileHandle is { IsInvalid: false, IsClosed: false };
    public string Serial { get; }
    public string PortName { get; }
    public IReadOnlyList<string> AvailableMediaIds => _availableMediaIds;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTryxPrinterTransport));
        }
        var copy = data.ToArray();
        lock (_writeLock)
        {
            // Async I/O on both directions: the handle is overlapped, so a sync Write
            // would collide with the background ReadAsync and stall the drain. Block on
            // the write task (the caller is a worker thread, no sync context to deadlock).
            // A failing write throws so the hub drops and rebuilds the transport (a real
            // unplug, or an interface reset the drain loop didn't prevent).
            _stream.WriteAsync(copy, 0, copy.Length).GetAwaiter().GetResult();
            _stream.FlushAsync().GetAwaiter().GetResult();
        }
    }

    // Continuously drain the panel's IN endpoint so its pipe never backs up. The
    // payload is discarded; the read completing is the only thing that matters. A
    // read error means the interface reset or the handle closed - stop, and the next
    // write failing lets the hub rebuild the transport (with a fresh drain loop).
    private async Task DrainReadsAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }
                // The panel pushes its stored-media list unprompted on this endpoint;
                // other reads (heartbeat acks, sensor replies) don't parse as one, so
                // a stale non-empty list is never overwritten by an unrelated read.
                // Assumes the list lands in a single read; a payload split across two
                // reads only parses the fragment carrying the "/userdata/default/" marker.
                var presets = TryxMediaList.ParsePresetIds(buffer.AsSpan(0, read));
                if (presets.Count > 0)
                {
                    _availableMediaIds = presets;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drainCts.Cancel();
        // Serialize against an in-flight Write so the stream can't close mid-write.
        lock (_writeLock)
        {
            _stream.Dispose();
        }
        try { _drainTask.Wait(500); } catch { /* drain loop already faulted/cancelled */ }
        _drainCts.Dispose();
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flags, IntPtr templateFile);
    }
}
#endif
