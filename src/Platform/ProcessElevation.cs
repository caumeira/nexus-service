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

    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

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
#endif
}
