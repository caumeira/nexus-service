using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Qos.Service.Platform.Mac;

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

    [DllImport("libproc.dylib")]
    private static extern int proc_listpids(uint type, uint typeinfo, [Out] int[]? buffer, int buffersize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, ref ProcTaskInfo info, int buffersize);

    [DllImport("libproc.dylib")]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint buffersize);
}
