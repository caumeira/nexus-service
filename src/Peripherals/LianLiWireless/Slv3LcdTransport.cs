using System;
using System.Collections.Generic;
#if WINDOWS
using System.IO;
using System.Runtime.InteropServices;
#endif

namespace Nexus.Service.Peripherals.LianLiWireless;

public interface ISlv3LcdDiscovery
{
    IReadOnlyList<Slv3LcdPortInfo> Discover();
}

public sealed class Slv3LcdPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public int ProductId { get; init; }
}

/// <summary>One open SL-LCD Wireless screen: a WinUSB bulk pipe pair, EP 0x01 OUT / EP 0x81 IN.</summary>
public interface ISlv3LcdTransport : IDisposable
{
    bool IsOpen { get; }
    string PortName { get; }

    /// <summary>Pushes a 400x400 JPEG as one fixed-size bulk write. False on a transport failure.</summary>
    bool PushImage(byte[] jpeg);

    /// <summary>Header-only write, no JPEG payload.</summary>
    bool SetBrightness(byte value);

    /// <summary>Header-only write; value is 0..3 for the four panel rotations.</summary>
    bool SetRotation(byte value);
}

#if WINDOWS
/// <summary>
/// WinUSB-backed transport for one SL-LCD Wireless screen. Reuses
/// <see cref="Slv3WinUsbInterop"/>, the same WinUSB P/Invoke surface as the
/// RF dongles; this device family differs only in protocol and endpoint
/// type (bulk here, interrupt on the dongles), not in the open/pipe-policy
/// pattern.
/// </summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    // A 102400-byte bulk write is a single WinUsb_WritePipe call; this bounds
    // a stalled write instead of blocking indefinitely.
    private const uint WriteTimeoutMs = 3000;
    // The ack read is optional (plan section 4.1); a short timeout reads a
    // non-acking screen as "no ack" rather than blocking the caller.
    private const uint AckReadTimeoutMs = 500;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly object _ioLock = new();
    private bool _disposed;

    public Slv3LcdTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        PortName = devicePath;

        _fileHandle = Slv3WinUsbInterop.CreateFileW(
            devicePath,
            Slv3WinUsbInterop.GENERIC_READ | Slv3WinUsbInterop.GENERIC_WRITE,
            Slv3WinUsbInterop.FILE_SHARE_READ | Slv3WinUsbInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Slv3WinUsbInterop.OPEN_EXISTING,
            Slv3WinUsbInterop.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (_fileHandle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }

        if (!Slv3WinUsbInterop.WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"WinUsb_Initialize failed for {devicePath}: {err}");
        }

        var writeTimeout = WriteTimeoutMs;
        var readTimeout = AckReadTimeoutMs;
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.WritePipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (write timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.ReadPipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref readTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (read timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
    }

    public bool IsOpen => !_disposed && !_fileHandle.IsInvalid && !_fileHandle.IsClosed;
    public string PortName { get; }

    public bool PushImage(byte[] jpeg)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var buffer = Slv3LcdProtocol.BuildPushJpgBuffer(timestamp, jpeg);
        lock (_ioLock)
        {
            var ok = Slv3WinUsbInterop.WinUsb_WritePipe(
                _winUsbHandle, Slv3LcdProtocol.WritePipeId, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
            if (ok)
            {
                TryDrainAckLocked();
            }
            return ok;
        }
    }

    public bool SetBrightness(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.Rotate, value);

    private bool WriteArgCommand(Slv3LcdProtocol.CmdType cmd, byte value)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(cmd, timestamp, value);
        lock (_ioLock)
        {
            var ok = Slv3WinUsbInterop.WinUsb_WritePipe(
                _winUsbHandle, Slv3LcdProtocol.WritePipeId, header, (uint)header.Length, out _, IntPtr.Zero);
            if (ok)
            {
                TryDrainAckLocked();
            }
            return ok;
        }
    }

    // A screen that never acks must not fail the push (plan section 4.1: the
    // ack is optional). Caller holds _ioLock.
    private void TryDrainAckLocked()
    {
        var ack = new byte[Slv3LcdProtocol.HeaderCipherSize];
        Slv3WinUsbInterop.WinUsb_ReadPipe(
            _winUsbHandle, Slv3LcdProtocol.ReadPipeId, ack, (uint)ack.Length, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_ioLock)
        {
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
        }
    }
}
#else
/// <summary>Inert: the LCD screens are Windows-only for v1 (see plans/lianli-wireless-support.md).</summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    public Slv3LcdTransport(string devicePath)
    {
        PortName = devicePath;
    }

    public bool IsOpen => false;
    public string PortName { get; }
    public bool PushImage(byte[] jpeg) => false;
    public bool SetBrightness(byte value) => false;
    public bool SetRotation(byte value) => false;
    public void Dispose()
    {
    }
}
#endif
