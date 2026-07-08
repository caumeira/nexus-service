using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Activity.Native;

/// <summary>
/// P/Invoke wrapper over <c>GetExtendedTcpTable</c> (iphlpapi.dll) - the same
/// kernel table netstat renders, read in-process. AOT-friendly via
/// LibraryImport. Compiles cross-platform (the only caller,
/// <see cref="Nexus.Service.Activity.WindowsNetworkProvider"/>, is registered
/// on Windows only).
/// </summary>
internal static partial class IpHlpApi
{
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    // TCP_TABLE_CLASS: connections only (excludes listeners, like `netstat -n`).
    private const int TcpTableOwnerPidConnections = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MibTcp6RowOwnerPid
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref uint pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        uint ulAf, int tableClass, uint reserved);

    /// <summary>
    /// Adds the PIDs owning at least one TCP connection (IPv4 or IPv6) to a
    /// non-loopback, non-unspecified peer. PID 0 rows (kernel-owned, e.g.
    /// TIME_WAIT leftovers) are skipped.
    /// </summary>
    public static void CollectInternetActivePids(HashSet<int> pids)
    {
        Collect(pids, AfInet);
        Collect(pids, AfInet6);
    }

    private static unsafe void Collect(HashSet<int> pids, uint af)
    {
        uint size = 0;
        var ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidConnections, 0);
        if (ret != ErrorInsufficientBuffer || size == 0)
        {
            return;
        }

        // Slack absorbs connections opened between the size query and the fetch.
        size += 4096;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, af, TcpTableOwnerPidConnections, 0) != NoError)
            {
                return;
            }

            var count = *(uint*)buffer;
            var rows = (byte*)buffer + sizeof(uint);
            if (af == AfInet)
            {
                for (uint i = 0; i < count; i++)
                {
                    var row = (MibTcpRowOwnerPid*)(rows + i * sizeof(MibTcpRowOwnerPid));
                    if (row->OwningPid > 0 && TcpPeerFilter.IsInternetPeerV4(row->RemoteAddr))
                    {
                        pids.Add((int)row->OwningPid);
                    }
                }
            }
            else
            {
                for (uint i = 0; i < count; i++)
                {
                    var row = (MibTcp6RowOwnerPid*)(rows + i * sizeof(MibTcp6RowOwnerPid));
                    if (row->OwningPid > 0
                        && TcpPeerFilter.IsInternetPeerV6(new ReadOnlySpan<byte>(row->RemoteAddr, 16)))
                    {
                        pids.Add((int)row->OwningPid);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
