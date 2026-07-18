using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity.Native;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sockets;

namespace Nexus.Service.Activity;

/// <summary>
/// Windows network monitor. Reads the TCP connection table in-process
/// (<c>GetExtendedTcpTable</c>) to discover which processes have active
/// connections to a non-loopback peer (loopback PIDs are dropped so the web
/// client / panel kiosk talking to nexus-service over localhost do not
/// appear as network users), then reads cumulative I/O byte counters via
/// <c>GetProcessIoCounters</c>. Only samples when subscribers exist.
/// </summary>
public sealed class WindowsNetworkProvider : BackgroundService, INetworkProvider
{
    private volatile IReadOnlyList<NetworkProcessInfo> _snapshot = Array.Empty<NetworkProcessInfo>();
    private int _intervalMs = 1000;
    private readonly MultiplexHub _hub;

    public WindowsNetworkProvider(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<NetworkProcessInfo> GetSnapshot(bool allowOnDemandSample = true) => _snapshot;
    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    private bool HasSubscribers =>
        _hub.TopicHasSubscribers("network") || _hub.TopicHasSubscribers("monitoring");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasSubscribers) Sample();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[network-win] sample failed: {ex.Message}");
            }

            try { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Sample()
    {
        // Step 1: PIDs whose TCP connections include a non-loopback peer.
        // Pure-loopback PIDs (nexus-service itself, the panel/Y70 launchers)
        // are dropped here so they never reach the per-process I/O counters.
        var activePids = new HashSet<int>();
        IpHlpApi.CollectInternetActivePids(activePids);
        if (activePids.Count == 0)
        {
            _snapshot = Array.Empty<NetworkProcessInfo>();
            return;
        }

        // Step 2: Resolve PIDs to process names and get cumulative I/O counters
        var current = new Dictionary<string, (long bytesIn, long bytesOut)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in activePids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var name = proc.ProcessName;
                if (GetProcessIoCounters(proc.Handle, out var counters))
                {
                    var read = (long)counters.ReadTransferCount;
                    var write = (long)counters.WriteTransferCount;
                    if (current.TryGetValue(name, out var prev))
                        current[name] = (prev.bytesIn + read, prev.bytesOut + write);
                    else
                        current[name] = (read, write);
                }
            }
            catch { } // Process may have exited or access denied
        }

        // Step 3: Store cumulative snapshot (frontend calculates rates from deltas)
        _snapshot = current
            .Where(kv => kv.Value.bytesIn + kv.Value.bytesOut > 0)
            .OrderByDescending(kv => kv.Value.bytesIn + kv.Value.bytesOut)
            .Select(kv => new NetworkProcessInfo
            {
                Name = kv.Key,
                BytesIn = kv.Value.bytesIn,
                BytesOut = kv.Value.bytesOut,
            })
            .ToList();
    }

    // Win32 P/Invoke for process I/O counters
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);
}
