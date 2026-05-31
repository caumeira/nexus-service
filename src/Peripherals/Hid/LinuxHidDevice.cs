using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Linux hidraw-backed <see cref="IHidDevice"/>. The counterpart to
/// <see cref="WindowsHidDevice"/>: feature reports go through HIDIOCSFEATURE /
/// HIDIOCGFEATURE ioctls and output/input reports through plain write()/read()
/// on a <c>/dev/hidrawN</c> fd. This mirrors what hidapi (and therefore the
/// OpenRGB controller this keeb work is ported from) does on Linux, so the same
/// report buffers that work on Windows work here unchanged: byte 0 is the report
/// id (0x00 for the keeb's un-numbered reports).
///
/// AOT-safe: blittable <c>[LibraryImport]</c> P/Invoke into libc, no reflection.
/// The service runs as root on Linux and <c>/dev/hidraw*</c> is world-rw, so no
/// permission dance is needed.
/// </summary>
public sealed partial class LinuxHidDevice : IHidDevice
{
    private int _fd;
    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal LinuxHidDevice(int fd, string path, int vid, int pid, string? serial, int usagePage, int usage)
    {
        _fd = fd;
        Path = path;
        VendorId = vid;
        ProductId = pid;
        Serial = serial;
        UsagePage = usagePage;
        Usage = usage;
    }

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (_fd < 0 || report.Length == 0) return false;
        var buf = report.ToArray();
        unsafe
        {
            fixed (byte* p = buf)
                return ioctl(_fd, HidIocSFeature(buf.Length), (nint)p) >= 0;
        }
    }

    public bool GetFeature(Span<byte> buffer)
    {
        if (_fd < 0 || buffer.Length == 0) return false;
        var buf = new byte[buffer.Length];
        buf[0] = buffer[0]; // report id preset on input
        int rc;
        unsafe
        {
            fixed (byte* p = buf)
                rc = ioctl(_fd, HidIocGFeature(buf.Length), (nint)p);
        }
        if (rc < 0) return false;
        buf.AsSpan().CopyTo(buffer);
        return true;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (_fd < 0 || report.Length == 0) return false;
        var buf = report.ToArray();
        unsafe
        {
            // hidraw write() is atomic per report and returns the bytes written
            // (or -1). Match hidapi: treat any non-negative result as success
            // rather than a strict length compare, so a benign count difference
            // can't false-fail the 30 Hz RGB stream.
            fixed (byte* p = buf)
                return (long)write(_fd, (nint)p, (nuint)buf.Length) >= 0;
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_fd < 0 || buffer.Length == 0) return 0;
        var pfd = new PollFd { fd = _fd, events = POLLIN, revents = 0 };
        var pr = poll(ref pfd, 1, timeoutMs);
        if (pr <= 0 || (pfd.revents & POLLIN) == 0) return 0; // timeout / error
        var buf = new byte[buffer.Length];
        long n;
        unsafe
        {
            fixed (byte* p = buf)
                n = (long)read(_fd, (nint)p, (nuint)buf.Length);
        }
        if (n <= 0) return 0;
        buf.AsSpan(0, (int)n).CopyTo(buffer);
        return (int)n;
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            try { close(_fd); } catch { }
            _fd = -1;
        }
    }

    // ── hidraw ioctl numbers ──
    // _IOC(dir,type,nr,size) = (dir<<30)|(size<<16)|(type<<8)|nr ; type 'H' = 0x48.
    // HIDIOCSFEATURE(len) = _IOWR('H', 0x06, len) ; HIDIOCGFEATURE(len) = _IOWR('H', 0x07, len).
    private static nuint HidIocSFeature(int len) => IowrH(0x06, len);
    private static nuint HidIocGFeature(int len) => IowrH(0x07, len);

    private static nuint IowrH(int nr, int len)
        => (nuint)(0xC0000000u | (((uint)len & 0x3FFF) << 16) | (0x48u << 8) | (uint)nr);

    private const short POLLIN = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, nint arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint write(int fd, nint buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint read(int fd, nint buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(ref PollFd fds, nuint nfds, int timeout);
}
