using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

internal static class MachStats
{
    private const int HOST_CPU_LOAD_INFO = 3;
    private const int HOST_VM_INFO64 = 4;
    private const int KERN_SUCCESS = 0;
    private const int SC_PAGESIZE = 29;

    [StructLayout(LayoutKind.Sequential)]
    public struct CpuLoadInfo
    {
        public uint UserTicks;
        public uint SystemTicks;
        public uint IdleTicks;
        public uint NiceTicks;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VmStats
    {
        public uint FreePages;
        public uint ActivePages;
        public uint InactivePages;
        public uint WiredPages;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong Pageins;
        public ulong Pageouts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeablePages;
        public uint SpeculativePages;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong Swapins;
        public ulong Swapouts;
        public uint CompressorPages;
        public uint ThrottledPages;
        public uint ExternalPages;
        public uint InternalPages;
        public ulong TotalUncompressedPagesInCompressor;
    }

    public static bool TryGetCpuLoad(out CpuLoadInfo info)
    {
        info = default;
        int count = 4; // sizeof(CpuLoadInfo) / sizeof(int)
        var host = mach_host_self();
        return host_statistics(host, HOST_CPU_LOAD_INFO, ref info, ref count) == KERN_SUCCESS;
    }

    public static bool TryGetVmStats(out VmStats stats)
    {
        stats = default;
        int count = 38; // sizeof(vm_statistics64) / sizeof(integer_t)
        var host = mach_host_self();
        return host_statistics64(host, HOST_VM_INFO64, ref stats, ref count) == KERN_SUCCESS;
    }

    public static long GetPageSize() => sysconf(SC_PAGESIZE);

    public static long GetPhysicalMemory()
    {
        nuint size = 8;
        long value = 0;
        return sysctlbyname("hw.memsize", ref value, ref size, IntPtr.Zero, 0) == 0 ? value : 0;
    }

    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics(uint host, int flavor, ref CpuLoadInfo info, ref int count);

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, ref VmStats info, ref int count);

    [DllImport("libSystem.dylib")]
    private static extern long sysconf(int name);

    [DllImport("libSystem.dylib")]
    private static extern int sysctlbyname(string name, ref long oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);
}
