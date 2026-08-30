#if WINDOWS
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// WinUSB bulk OUT transport for the Kraken LCD.
///
/// The cooler advertises WinUSB through its own MS OS descriptors, so Windows binds the
/// in-box winusb.inf automatically and no driver install is needed on a customer machine.
/// The interface GUID it publishes is fixed per model.
///
/// P/Invoke signatures are shared with the Lian Li wireless LCD rather than duplicated.
/// </summary>
public sealed class WindowsKrakenLcdTransport : IKrakenLcdTransport
{
    // A full 640x640 RGBA frame is ~1.6 MB split into chunked writes; this bounds a
    // stalled write rather than blocking the caller indefinitely.
    private const uint WriteTimeoutMs = 5000;
    private const uint WaitObject0 = 0;
    private const byte BulkOutPipeId = 0x02;

    private readonly SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly IntPtr _writeEvent;
    private readonly object _ioLock = new();
    private bool _disposed;

    public WindowsKrakenLcdTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

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

        _writeEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (_writeEvent == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            _fileHandle.Dispose();
            throw new IOException($"CreateEventW failed for {devicePath}: {err}");
        }

        var writeTimeout = WriteTimeoutMs;
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, BulkOutPipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout))
        {
            ServiceLog.Warn($"[nzxt-kraken] SetPipePolicy (write timeout) failed: {Marshal.GetLastWin32Error()}");
        }

        // Clears a halt left by NZXT CAM's prior session; a stalled bulk OUT pipe
        // rejects WinUsb_WritePipe with ERROR_BAD_COMMAND.
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, BulkOutPipeId);
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length == 0)
        {
            return false;
        }
        var buffer = data.ToArray();
        lock (_ioLock)
        {
            // Re-check inside the lock: the check above can pass while Dispose is waiting
            // on _ioLock, and by the time we get it WinUsb_Free has already run. Writing
            // through a freed handle is an access violation, not an exception.
            if (_disposed)
            {
                return false;
            }
            return WriteOverlappedLocked(buffer);
        }
    }

    private bool WriteOverlappedLocked(byte[] buffer)
    {
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _writeEvent }, ovPtr, false);
            Slv3WinUsbInterop.ResetEvent(_writeEvent);
            var ok = Slv3WinUsbInterop.WinUsb_WritePipe(
                _winUsbHandle, BulkOutPipeId, pin.AddrOfPinnedObject(), (uint)buffer.Length, out var transferred, ovPtr);
            if (!ok && Marshal.GetLastWin32Error() == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
            {
                if (Slv3WinUsbInterop.WaitForSingleObject(_writeEvent, WriteTimeoutMs) != WaitObject0)
                {
                    Slv3WinUsbInterop.WinUsb_AbortPipe(_winUsbHandle, BulkOutPipeId);
                }
                ok = Slv3WinUsbInterop.WinUsb_GetOverlappedResult(_winUsbHandle, ovPtr, out transferred, wait: true);
            }
            return ok && transferred == buffer.Length;
        }
        finally
        {
            Marshal.FreeHGlobal(ovPtr);
            pin.Free();
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_writeEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_writeEvent);
            }
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
        }
    }
}

public sealed class WindowsKrakenLcdTransportFactory : IKrakenLcdTransportFactory
{
    // Published by the cooler's own MS OS descriptors; the trailing digits encode the PID.
    private static readonly Guid KrakenEliteV2InterfaceGuid = new("30123011-7EE7-1125-0724-101503010819");

    public IKrakenLcdTransport? Open(string? serial)
    {
        var path = FindDevicePath(serial);
        if (path == null)
        {
            return null;
        }
        try
        {
            return new WindowsKrakenLcdTransport(path);
        }
        catch (IOException ex)
        {
            ServiceLog.Warn($"[nzxt-kraken] LCD bulk open failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindDevicePath(string? serial)
    {
        var guid = KrakenEliteV2InterfaceGuid;
        var devInfo = Slv3WinUsbInterop.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
            Slv3WinUsbInterop.DIGCF_PRESENT | Slv3WinUsbInterop.DIGCF_DEVICEINTERFACE);
        if (devInfo == (IntPtr)(-1))
        {
            return null;
        }
        try
        {
            var idx = 0u;
            var ifaceData = new Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA();
            ifaceData.cbSize = Marshal.SizeOf(ifaceData);
            string? firstMatch = null;

            while (Slv3WinUsbInterop.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, idx, ref ifaceData))
            {
                idx++;
                var path = ReadDevicePath(devInfo, ref ifaceData);
                if (string.IsNullOrEmpty(path) || !MatchesKraken(path))
                {
                    continue;
                }
                firstMatch ??= path;
                // The serial appears in the composite parent's segment of the path.
                if (!string.IsNullOrEmpty(serial) &&
                    path.Contains(serial, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return firstMatch;
        }
        finally
        {
            Slv3WinUsbInterop.SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    private static bool MatchesKraken(string path)
    {
        var vidIdx = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        var pidIdx = path.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vidIdx < 0 || pidIdx < 0 || vidIdx + 8 > path.Length || pidIdx + 8 > path.Length)
        {
            return false;
        }
        return int.TryParse(path.AsSpan(vidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            && int.TryParse(path.AsSpan(pidIdx + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid)
            && vid == KrakenProtocol.VendorId
            && KrakenProtocol.ProductIds.Contains(pid);
    }

    private static string ReadDevicePath(IntPtr devInfo, ref Slv3WinUsbInterop.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return "";
        }
        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!Slv3WinUsbInterop.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifaceData, detail, required, out _, IntPtr.Zero))
            {
                return "";
            }
            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }
}
#endif
