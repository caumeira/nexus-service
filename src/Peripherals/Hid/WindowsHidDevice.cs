#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>Windows HID device handle wrapper. Non-overlapped feature-report API.</summary>
public sealed class WindowsHidDevice : IHidDevice
{
    private IntPtr _handle;
    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal WindowsHidDevice(IntPtr handle, string path, int vid, int pid, string? serial, int usagePage, int usage)
    {
        _handle = handle;
        Path = path;
        VendorId = vid;
        ProductId = pid;
        Serial = serial;
        UsagePage = usagePage;
        Usage = usage;
    }

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = report.ToArray();
        return WindowsHidEnumerator.Native.HidD_SetFeature(_handle, buf, (uint)buf.Length);
    }

    public bool GetFeature(Span<byte> buffer)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = new byte[buffer.Length];
        buf[0] = buffer[0]; // report id must be preset on input
        var ok = WindowsHidEnumerator.Native.HidD_GetFeature(_handle, buf, (uint)buf.Length);
        if (ok)
        {
            buf.AsSpan().CopyTo(buffer);
        }
        return ok;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = report.ToArray();
        return WindowsHidEnumerator.Native.WriteFile(_handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_handle == IntPtr.Zero) return 0;
        // Blocking read (non-overlapped). Vendor protocols here are
        // feature-report driven, so this path is rarely hit.
        var buf = new byte[buffer.Length];
        if (!WindowsHidEnumerator.Native.ReadFile(_handle, buf, (uint)buf.Length, out var read, IntPtr.Zero))
        {
            return 0;
        }
        buf.AsSpan(0, (int)read).CopyTo(buffer);
        return (int)read;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero && _handle != (IntPtr)(-1))
        {
            WindowsHidEnumerator.Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
#endif
