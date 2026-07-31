using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Platform.Mac;

internal static class MacProcInfo
{
    private const uint PROC_ALL_PIDS = 1;
    private const int PROC_PIDTASKINFO = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcTaskInfo
    {
        public ulong VirtualSize;
        public ulong ResidentSize;
        public ulong TotalUser;
        public ulong TotalSystem;
        public ulong ThreadsUser;
        public ulong ThreadsSystem;
        public int Policy;
        public int Faults;
        public int Pageins;
        public int CowFaults;
        public int MessagesSent;
        public int MessagesReceived;
        public int SyscallsMach;
        public int SyscallsUnix;
        public int Csw;
        public int ThreadNum;
        public int NumRunning;
        public int Priority;
    }

    public static int[] ListPids()
    {
        int size = proc_listpids(PROC_ALL_PIDS, 0, null, 0);
        if (size <= 0)
            return Array.Empty<int>();

        size += size / 4;
        var buffer = new int[size / sizeof(int)];
        int actual = proc_listpids(PROC_ALL_PIDS, 0, buffer, buffer.Length * sizeof(int));
        if (actual <= 0)
            return Array.Empty<int>();

        int count = actual / sizeof(int);
        if (count < buffer.Length)
            Array.Resize(ref buffer, count);
        return buffer;
    }

    public static bool TryGetTaskInfo(int pid, out ProcTaskInfo info)
    {
        info = default;
        int ret = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, ref info, 96);
        return ret == 96;
    }

    // ProcTaskInfo.TotalUser/TotalSystem are mach absolute-time ticks, not
    // nanoseconds: ns = ticks * (1e9 / hw.tbfrequency). The tick rate is 1 GHz
    // on Intel but not on Apple Silicon, where reading ticks as ns
    // under-reports CPU time by more than an order of magnitude. The factor
    // comes from the hw.tbfrequency sysctl rather than mach_timebase_info:
    // under Rosetta 2 the latter reports 1/1 (the translated-process view of
    // mach_absolute_time) while the kernel's per-task counters stay in native
    // ticks; the sysctl reports the native tick rate in both worlds. Lazy so
    // the P/Invoke never runs at type-init on the RID-less Windows/Linux
    // builds that keep this class compiled.
    private static readonly Lazy<double> MachTicksToNsFactor = new(ReadTimebaseFactor);

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebaseInfo
    {
        public uint Numer;
        public uint Denom;
    }

    private static double ReadTimebaseFactor()
    {
        nuint size = 8;
        long freq = 0;
        if (sysctlbyname("hw.tbfrequency", ref freq, ref size, IntPtr.Zero, 0) == 0 && freq > 0)
            return 1e9 / freq;
        var info = default(MachTimebaseInfo);
        return mach_timebase_info(ref info) == 0 && info.Denom != 0
            ? (double)info.Numer / info.Denom
            : 1.0;
    }

    /// <summary>Converts mach absolute-time ticks (the unit of
    /// ProcTaskInfo.TotalUser/TotalSystem) to nanoseconds.</summary>
    public static double MachTicksToNs(ulong ticks) => ticks * MachTicksToNsFactor.Value;

    public static string GetProcessName(int pid, byte[] pathBuf)
    {
        int len = proc_pidpath(pid, pathBuf, (uint)pathBuf.Length);
        if (len <= 0)
            return "";

        int lastSlash = -1;
        for (int i = len - 1; i >= 0; i--)
        {
            if (pathBuf[i] == (byte)'/')
            {
                lastSlash = i;
                break;
            }
        }

        int nameStart = lastSlash + 1;
        return Encoding.UTF8.GetString(pathBuf, nameStart, len - nameStart);
    }

    // RUSAGE_INFO_V2 per sys/resource.h: proc_pid_rusage has no buffersize
    // parameter, so the struct here must match rusage_info_v2's layout
    // exactly (the kernel writes based on the flavor alone) - ri_uuid is
    // kept as two ulong fields since its content is never read, only its
    // 16-byte width for layout purposes.
    private const int RUSAGE_INFO_V2 = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfoV2
    {
        public ulong UuidLo;
        public ulong UuidHi;
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PkgIdleWkups;
        public ulong InterruptWkups;
        public ulong Pageins;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysFootprint;
        public ulong ProcStartAbstime;
        public ulong ProcExitAbstime;
        public ulong ChildUserTime;
        public ulong ChildSystemTime;
        public ulong ChildPkgIdleWkups;
        public ulong ChildInterruptWkups;
        public ulong ChildPageins;
        public ulong ChildElapsedAbstime;
        public ulong DiskIoBytesRead;
        public ulong DiskIoBytesWritten;
    }

    /// <summary>Cumulative disk read+write bytes for pid via proc_pid_rusage.
    /// False (values zeroed) on any failure - permission denial or a pid
    /// that exited between listing and this call - never throws.</summary>
    public static bool TryGetDiskIoBytes(int pid, out ulong bytesRead, out ulong bytesWritten)
    {
        var info = new RUsageInfoV2();
        int ret = proc_pid_rusage(pid, RUSAGE_INFO_V2, ref info);
        if (ret != 0)
        {
            bytesRead = 0;
            bytesWritten = 0;
            return false;
        }
        bytesRead = info.DiskIoBytesRead;
        bytesWritten = info.DiskIoBytesWritten;
        return true;
    }

    [DllImport("libproc.dylib")]
    private static extern int proc_listpids(uint type, uint typeinfo, [Out] int[]? buffer, int buffersize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, ref ProcTaskInfo info, int buffersize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint buffersize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RUsageInfoV2 buffer);

    [DllImport("libSystem.dylib")]
    private static extern int mach_timebase_info(ref MachTimebaseInfo info);

    [DllImport("libSystem.dylib")]
    private static extern int sysctlbyname(string name, ref long oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
}
