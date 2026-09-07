using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

internal enum ServiceStopResult
{
    Stopped,    // stop control accepted, or service was already stopped
    NotFound,   // service is not installed (ERROR_SERVICE_DOES_NOT_EXIST)
    Failed,     // service found but stop could not be sent
}

[SupportedOSPlatform("windows")]
internal static class WindowsServiceController
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_CONTROL_STOP = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    private static extern bool EnumServicesStatusEx(IntPtr hSCManager, int infoLevel, uint dwServiceType,
        uint dwServiceState, IntPtr lpServices, uint cbBufSize, out uint pcbBytesNeeded,
        out uint lpServicesReturned, ref uint lpResumeHandle, string? pszGroupName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    /// <summary>
    /// Every Win32 service registered with the SCM, by key and display name,
    /// whatever its state. Empty on any failure and off Windows; never throws.
    /// Callers treat empty as "nothing found", so a refusal never reads as a
    /// positive.
    /// </summary>
    public static IReadOnlyList<(string Key, string DisplayName)> ListServices()
    {
        var result = new List<(string, string)>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }
        try
        {
            var hScm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (hScm == IntPtr.Zero)
            {
                return result;
            }
            try
            {
                uint resume = 0;
                // One sizing call, then one read. A service installed between
                // the two is simply missed, which the install default tolerates.
                EnumServicesStatusEx(hScm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                    IntPtr.Zero, 0, out var needed, out _, ref resume, null);
                if (needed == 0)
                {
                    return result;
                }
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    resume = 0;
                    if (!EnumServicesStatusEx(hScm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                            buffer, needed, out _, out var count, ref resume, null))
                    {
                        return result;
                    }
                    var stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                    for (var i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + (i * stride));
                        result.Add((
                            Marshal.PtrToStringUni(entry.lpServiceName) ?? "",
                            Marshal.PtrToStringUni(entry.lpDisplayName) ?? ""));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(hScm);
            }
        }
        catch
        {
            return result;
        }
        return result;
    }

    /// <summary>
    /// Sends SERVICE_CONTROL_STOP to the named Windows service.
    /// Returns NotFound when the service is not installed, Stopped when the stop was
    /// accepted or the service was already stopped, Failed when it exists but the
    /// stop could not be sent. Never throws.
    /// </summary>
    public static ServiceStopResult StopService(string name)
    {
        try
        {
            var hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (hScm == IntPtr.Zero)
            {
                return ServiceStopResult.Failed;
            }
            try
            {
                var hSvc = OpenService(hScm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (hSvc == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                        ? ServiceStopResult.NotFound
                        : ServiceStopResult.Failed;
                }
                try
                {
                    if (ControlService(hSvc, SERVICE_CONTROL_STOP, out _))
                    {
                        return ServiceStopResult.Stopped;
                    }
                    // Already stopped counts as success.
                    return Marshal.GetLastWin32Error() == ErrorServiceNotActive
                        ? ServiceStopResult.Stopped
                        : ServiceStopResult.Failed;
                }
                finally
                {
                    CloseServiceHandle(hSvc);
                }
            }
            finally
            {
                CloseServiceHandle(hScm);
            }
        }
        catch
        {
            return ServiceStopResult.Failed;
        }
    }
}
