using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Sockets;
#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace Nexus.Service.Activity;

/// <summary>
/// Per-process GPU usage via the Windows PDH "GPU Engine" + "GPU Process Memory"
/// performance counters -- vendor-agnostic, the same source Task Manager's GPU
/// column uses. Engine utilization is summed across all engines/adapters per
/// process; dedicated VRAM is summed per process. Subscription-gated like
/// <see cref="ProcessMonitor"/>; on non-Windows the snapshot stays empty.
/// </summary>
public sealed class GpuProcessMonitor : BackgroundService
{
    private readonly MultiplexHub _hub;
    private volatile IReadOnlyList<GpuProcessEntry> _latest = Array.Empty<GpuProcessEntry>();

    public GpuProcessMonitor(MultiplexHub hub) => _hub = hub;

    public IReadOnlyList<GpuProcessEntry> GetSnapshot() => _latest;

    private bool HasSubscribers => _hub.TopicHasSubscribers("gpu-processes");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
#if WINDOWS
        await Task.Delay(2000, ct);
        IntPtr query = IntPtr.Zero, engine = IntPtr.Zero, mem = IntPtr.Zero;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (HasSubscribers)
                {
                    if (query == IntPtr.Zero)
                    {
                        // Open the query + add the wildcard counters; the first
                        // collect seeds the baseline the rate counters need.
                        if (Pdh.Open(out query, out engine, out mem))
                        {
                            Pdh.Collect(query);
                        }
                    }
                    else
                    {
                        Pdh.Collect(query);
                        _latest = Sample(engine, mem);
                    }
                }
                else if (query != IntPtr.Zero)
                {
                    Pdh.Close(query);
                    query = engine = mem = IntPtr.Zero;
                    _latest = Array.Empty<GpuProcessEntry>();
                }

                try { await Task.Delay(1000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gpu-process-monitor] {ex.Message}");
        }
        finally
        {
            if (query != IntPtr.Zero) Pdh.Close(query);
        }
#else
        _ = HasSubscribers;
        await Task.CompletedTask;
#endif
    }

#if WINDOWS
    private static IReadOnlyList<GpuProcessEntry> Sample(IntPtr engine, IntPtr mem)
    {
        var util = new Dictionary<int, double>();
        foreach (var (inst, value) in Pdh.ReadArray(engine))
        {
            var pid = ParsePid(inst);
            if (pid > 0) util[pid] = (util.TryGetValue(pid, out var v) ? v : 0) + value;
        }
        var dedicated = new Dictionary<int, double>();
        foreach (var (inst, value) in Pdh.ReadArray(mem))
        {
            var pid = ParsePid(inst);
            if (pid > 0) dedicated[pid] = (dedicated.TryGetValue(pid, out var v) ? v : 0) + value;
        }

        var pids = new HashSet<int>(util.Keys);
        pids.UnionWith(dedicated.Keys);

        var rows = new List<GpuProcessEntry>();
        foreach (var pid in pids)
        {
            var gpu = Math.Min(100, util.TryGetValue(pid, out var u) ? u : 0);
            var dedMb = (dedicated.TryGetValue(pid, out var m) ? m : 0) / (1024.0 * 1024.0);
            if (gpu < 0.5 && dedMb < 5) continue; // drop near-idle processes
            var name = ProcName(pid);
            if (name.Length == 0) continue;
            rows.Add(new GpuProcessEntry { Name = name, GpuPercent = gpu, DedicatedMb = dedMb });
        }

        // Group same-named processes (an app split across pids), then top 12.
        return rows
            .GroupBy(r => r.Name)
            .Select(g => new GpuProcessEntry
            {
                Name = g.Key,
                GpuPercent = Math.Min(100, g.Sum(x => x.GpuPercent)),
                DedicatedMb = g.Sum(x => x.DedicatedMb),
            })
            .OrderByDescending(e => e.GpuPercent)
            .ThenByDescending(e => e.DedicatedMb)
            .Take(12)
            .ToList();
    }

    // Instance names look like: pid_5040_luid_0x..._0x..._phys_0_eng_0_engtype_3d
    private static int ParsePid(string inst)
    {
        const string tag = "pid_";
        var i = inst.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += tag.Length;
        var j = i;
        while (j < inst.Length && char.IsDigit(inst[j])) j++;
        return j > i && int.TryParse(inst.AsSpan(i, j - i), out var pid) ? pid : 0;
    }

    private static string ProcName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    private static class Pdh
    {
        private const string Lib = "pdh.dll";
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_MORE_DATA = 0x800007D2;
        private const uint OK = 0;
        // PDH_FMT_COUNTERVALUE_ITEM_W (x64): LPWSTR szName(8) + DWORD CStatus(4)
        // + 4 pad + double(8) = 24 bytes.
        private const int ItemSize = 24;

        [DllImport(Lib, CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
        [DllImport(Lib, CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport(Lib)]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport(Lib)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
        [DllImport(Lib)]
        private static extern uint PdhCloseQuery(IntPtr query);

        public static bool Open(out IntPtr query, out IntPtr engine, out IntPtr mem)
        {
            engine = mem = IntPtr.Zero;
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != OK) { query = IntPtr.Zero; return false; }
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out engine) != OK
                || PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out mem) != OK)
            {
                PdhCloseQuery(query); query = IntPtr.Zero; return false;
            }
            return true;
        }

        public static void Collect(IntPtr query) => PdhCollectQueryData(query);
        public static void Close(IntPtr query) { try { PdhCloseQuery(query); } catch { } }

        public static IEnumerable<(string inst, double value)> ReadArray(IntPtr counter)
        {
            uint size = 0;
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
                yield break;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, buf) != OK)
                    yield break;
                for (var i = 0; i < count; i++)
                {
                    var p = buf + i * ItemSize;
                    var namePtr = Marshal.ReadIntPtr(p);
                    var cstatus = Marshal.ReadInt32(p, 8);
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16));
                    if (namePtr == IntPtr.Zero || cstatus > 1) continue; // 0/1 = valid data
                    var name = Marshal.PtrToStringUni(namePtr);
                    if (!string.IsNullOrEmpty(name)) yield return (name, value);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
#endif
}
