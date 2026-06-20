#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// One process-wide Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
/// Direct child processes assigned to it (OpenRGB-headless, ffmpeg) are killed by
/// the OS the instant Nexus.exe exits - including a hard Environment.Exit on
/// shutdown or a crash - so the service never orphans them. The job handle is
/// deliberately never closed; it lives for the process lifetime, which is what
/// arms the kill-on-close.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ChildProcessJob
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly object s_lock = new();
    private static IntPtr s_job = IntPtr.Zero;
    private static bool s_failed;

    /// <summary>Assign a started child process to the kill-on-exit job. Best-effort.</summary>
    public static void Assign(Process process)
    {
        try
        {
            var job = EnsureJob();
            if (job == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Console.Error.WriteLine(
                    $"[child-job] assign failed err={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[child-job] assign threw: {ex.Message}");
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (s_lock)
        {
            if (s_job != IntPtr.Zero || s_failed) return s_job;

            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[child-job] CreateJobObject failed err={Marshal.GetLastWin32Error()}");
                s_failed = true;
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                Console.Error.WriteLine($"[child-job] SetInformationJobObject failed err={Marshal.GetLastWin32Error()}");
                s_failed = true;
                return IntPtr.Zero;
            }

            s_job = handle;
            return s_job;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
#endif
