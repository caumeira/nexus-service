using System;
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
