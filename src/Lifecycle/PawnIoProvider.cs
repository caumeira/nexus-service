using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Windows-only PawnIO driver detection. Checks two things:
///
/// 1. IsInstalled — checks the kernel service registry key at
///    HKLM\SYSTEM\CurrentControlSet\Services\PawnIO. Returns true if the
///    PawnIO kernel driver service is registered (regardless of whether
///    it was installed by us via PawnIoInstaller or by the user via
///    PawnIO_setup.exe). Uses Microsoft.Win32.Registry which is AOT-safe.
///
/// 2. IsOpen — tries to open the PawnIO kernel device handle at
///    \\?\GLOBALROOT\Device\PawnIO. If the handle succeeds, the driver
///    is loaded and running. The handle is closed immediately — we don't
///    keep it open because sensor reading goes through LibreHardwareMonitor.
///
/// On non-Windows platforms both properties return false.
/// </summary>
public sealed class PawnIoProvider : IPawnIoProvider
{
    public bool IsInstalled
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch { return false; }
        }
    }

    public bool IsOpen
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            try
            {
                var handle = CreateFileW(
                    @"\\?\GLOBALROOT\Device\PawnIO",
                    0xC0000000, // GENERIC_READ | GENERIC_WRITE
                    3,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,          // OPEN_EXISTING
                    0x80,       // FILE_ATTRIBUTE_NORMAL
                    IntPtr.Zero);

                if (handle == new IntPtr(-1)) return false;
                CloseHandle(handle);
                return true;
            }
            catch { return false; }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
