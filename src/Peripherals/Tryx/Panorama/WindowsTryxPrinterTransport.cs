#if WINDOWS
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// <see cref="ITryxPanoramaTransport"/> for the RK-firmware Panorama (VID 0x391A),
/// which binds to the Windows usbprint device class rather than CDC serial. Opened
/// with CreateFileW against the usbprint device interface path; the protocol is
/// write-only, so writes go straight through WriteFile with no read/response path.
/// The handle is a <see cref="SafeFileHandle"/> so an in-flight WriteFile can't race
/// a concurrent Dispose onto a recycled numeric handle, and a dropped reference
/// still frees the kernel handle.
/// </summary>
public sealed class WindowsTryxPrinterTransport : ITryxPanoramaTransport
{
    private readonly SafeFileHandle _handle;
    private readonly object _writeLock = new();
    private bool _disposed;

    public WindowsTryxPrinterTransport(string devicePath, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Serial = serial ?? "";
        PortName = devicePath;
        _handle = Native.CreateFileW(
            devicePath,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }
    }

    public bool IsOpen => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;
    public string Serial { get; }
    public string PortName { get; }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTryxPrinterTransport));
        }
        lock (_writeLock)
        {
            var copy = data.ToArray();
            // Retry a transient WriteFile on the same held handle rather than
            // reopening. The usbprint stack occasionally returns ERROR_GEN_FAILURE
            // under a busy multi-threaded caller; a short retry rides over it. A
            // still-failing write throws so the hub can drop and rebuild (a real
            // unplug), instead of the old reopen-on-every-failure churn.
            int lastError = 0;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (Native.WriteFile(_handle, copy, (uint)copy.Length, out _, IntPtr.Zero))
                {
                    return;
                }
                lastError = Marshal.GetLastWin32Error();
                Thread.Sleep(15);
            }
            throw new IOException($"WriteFile failed after retries: {lastError}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Serialize against an in-flight Write so CloseHandle can't recycle the
        // handle mid-WriteFile.
        lock (_writeLock)
        {
            _handle.Dispose();
        }
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flags, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);
    }
}
#endif
