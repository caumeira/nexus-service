#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
#endif

namespace Nexus.Service.Platform;

public readonly record struct ProcessElevationSnapshot(
    bool Supported,
    bool IsElevated,
    string Status);

public static class ProcessElevation
{
    private const string UnsupportedStatus = "unsupported";
    private const string ElevatedStatus = "elevated";
    private const string NotElevatedStatus = "not-elevated";
    private const string UnknownStatus = "unknown";

    public static ProcessElevationSnapshot GetCurrent()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsElevation(out var isElevated)
                ? new ProcessElevationSnapshot(true, isElevated, isElevated ? ElevatedStatus : NotElevatedStatus)
                : new ProcessElevationSnapshot(true, false, UnknownStatus);
        }
#endif

        return new ProcessElevationSnapshot(false, false, UnsupportedStatus);
    }

    /// <summary>Whether the process identified by pid is running elevated.
    /// False on any failure to open or query the process (exited, protected,
    /// access denied) and on non-Windows - never throws.</summary>
    public static bool IsProcessElevated(int pid)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return TryGetElevationForPid(pid);
        }
#endif
        return false;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsElevation(out bool isElevated)
    {
        isElevated = false;

        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            var tokenInfoLength = Marshal.SizeOf<TokenElevationInfo>();
            if (!GetTokenInformation(token, TokenElevation, out var elevation, tokenInfoLength, out _))
            {
                return false;
            }

            isElevated = elevation.TokenIsElevated != 0;
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetElevationForPid(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(handle, TokenQuery, out var token))
            {
                return false;
            }

            using (token)
            {
                var tokenInfoLength = Marshal.SizeOf<TokenElevationInfo>();
                if (!GetTokenInformation(token, TokenElevation, out var elevation, tokenInfoLength, out _))
                {
                    return false;
                }

                return elevation.TokenIsElevated != 0;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationInfo
    {
        public int TokenIsElevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out TokenElevationInfo tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#endif
}
