using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Activity;

/// <summary>
/// Background service that samples running processes about once a second
/// (configurable via SetInterval, minimum 200 ms) and exposes the latest
/// snapshot via GetProcesses(). Only samples when at least one WebSocket
/// client is subscribed to process or monitoring topics.
/// </summary>
public sealed class ProcessMonitor : BackgroundService
{
    private static readonly int ProcessorCount = Environment.ProcessorCount;
    private volatile IReadOnlyList<ProcessInfo> _latest = Array.Empty<ProcessInfo>();
    private int _intervalMs = 1000;
    private readonly MultiplexHub _hub;
    private readonly object _demandGate = new();
    private readonly HashSet<string> _demands = new(StringComparer.Ordinal);

    // Bounds growth over a long-running service: every distinct name ever
    // seen (installers, temp tools, updaters) would otherwise accumulate a
    // permanent entry with no eviction. _pathCacheOrder tracks insertion
    // order for eviction - Dictionary enumeration order is not reliable
    // insertion order once removals have happened (a freed slot is reused
    // by the next insert and can enumerate first, evicting on every insert).
    private const int PathCacheMaxEntries = 500;
    private readonly object _pathCacheLock = new();
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pathCacheOrder = new();

    // Delta tracking for CPU time (Windows uses TimeSpan, macOS uses nanoseconds).
    private readonly Dictionary<int, (TimeSpan cpuTime, DateTime when)> _winPrev = new();
    private readonly Dictionary<int, (ulong cpuNs, DateTime when)> _macPrev = new();

    public ProcessMonitor(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<ProcessInfo> GetProcesses() => _latest;

    /// <summary>Test-only seam: the sampling loop never runs synchronously
    /// under a unit test, so this stands in for a completed sample.</summary>
    internal void SetProcessesForTest(IReadOnlyList<ProcessInfo> processes) => _latest = processes;

    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    /// <summary>Resolves name (a live process's aggregate name) to the exe
    /// path of its newest matching pid, for icon extraction. Resolved
    /// on-demand (not every sampling tick - MainModule enumeration is far
    /// pricier than the CPU/memory reads every process already pays) and
    /// cached by name; access-denied on a specific pid is tolerated and
    /// simply yields no path rather than failing the request. Null when no
    /// live process matches or the path can't be read.</summary>
    public string? ResolveExecutablePath(string name)
    {
        lock (_pathCacheLock)
        {
            if (_pathCache.TryGetValue(name, out var cached))
            {
                return cached;
            }
        }

        var candidates = _latest.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var match = candidates
            .OrderByDescending(p => p.StartedAtMs ?? 0)
            .FirstOrDefault();
        if (match is null)
        {
            return null;
        }

        string? path = null;
        try
        {
            using var proc = Process.GetProcessById(match.Pid);
            path = proc.MainModule?.FileName;
        }
        catch { }

        if (!string.IsNullOrEmpty(path))
        {
            lock (_pathCacheLock)
            {
                if (!_pathCache.ContainsKey(name))
                {
                    _pathCacheOrder.Enqueue(name);
                }
                _pathCache[name] = path;
                if (_pathCache.Count > PathCacheMaxEntries)
                {
                    _pathCache.Remove(_pathCacheOrder.Dequeue());
                }
            }
        }
        return path;
    }

    /// <summary>Adds or removes source from the demand set that keeps
    /// sampling running even with no WebSocket subscriber (the metrics
    /// history sampler's always-on per-app recording). Idempotent per
    /// source id, same shape as IFpsProvider.SetDemand.</summary>
    public void SetDemand(string source, bool wanted)
    {
        lock (_demandGate)
        {
            if (wanted) _demands.Add(source);
            else _demands.Remove(source);
        }
    }

    private bool HasRealSubscribers =>
        _hub.TopicHasSubscribers("processes") || _hub.TopicHasSubscribers("monitoring");

    private bool HasDemand
    {
        get { lock (_demandGate) { return _demands.Count > 0; } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Demand with no real WS subscriber only needs app-history's
            // own sub-cadence, not the full 1Hz broadcast rate - sampling
            // every process every second, 24/7, for a consumer that reads
            // far less often is pure waste on every install that never
            // opens the processes UI.
            var hasReal = HasRealSubscribers;
            try
            {
                if (hasReal || HasDemand)
                {
#if MACOS
                    SampleMacOs();
#else
                    SampleWindows();
#endif
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[process-monitor] sample failed: {ex.Message}");
            }

            var delayMs = hasReal ? _intervalMs : MetricsHistory.AppSampleIntervalSeconds * 1000;
            try
            { await Task.Delay(delayMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

#if MACOS
    /// <summary>
    /// macOS: proc_pidinfo gives per-process CPU time and RSS via direct
    /// kernel syscalls - no subprocess spawn. Delta-based CPU% normalized
    /// by elapsed time and core count, same approach as the Windows path.
    /// </summary>
    private void SampleMacOs()
    {
        var pids = Platform.Mac.MacProcInfo.ListPids();
        if (pids.Length == 0)
            return;

        var now = DateTime.UtcNow;
        var result = new List<ProcessInfo>();
        var seen = new HashSet<int>();
        var pathBuf = new byte[4096];

        foreach (var pid in pids)
        {
            if (pid <= 0)
                continue;
            seen.Add(pid);

            if (!Platform.Mac.MacProcInfo.TryGetTaskInfo(pid, out var ti))
                continue;

            var name = Platform.Mac.MacProcInfo.GetProcessName(pid, pathBuf);
            if (string.IsNullOrEmpty(name))
                continue;

            var cpuNs = ti.TotalUser + ti.TotalSystem;
            double cpuPercent = 0;
            if (_macPrev.TryGetValue(pid, out var prev))
            {
                var elapsedMs = (now - prev.when).TotalMilliseconds;
                if (elapsedMs > 50)
                {
                    var deltaNs = cpuNs > prev.cpuNs ? cpuNs - prev.cpuNs : 0;
                    var deltaMs = deltaNs / 1_000_000.0;
                    cpuPercent = Math.Clamp(deltaMs / elapsedMs / ProcessorCount * 100.0, 0, 100);
                }
            }
            _macPrev[pid] = (cpuNs, now);

            result.Add(new ProcessInfo
            {
                Pid = pid,
                Name = name,
                CpuPercent = Math.Round(cpuPercent, 1),
                MemoryMb = Math.Round(ti.ResidentSize / (1024.0 * 1024.0), 1),
                CpuTimeSeconds = Math.Round((ti.TotalUser + ti.TotalSystem) / 1_000_000_000.0, 1),
            });
        }

        // Prune stale PID entries.
        if (_macPrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_macPrev.Count - seen.Count);
            foreach (var k in _macPrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _macPrev.Remove(k);
            }
        }

        // Group by name: the first instance per name accumulates the rest.
        var grouped = new Dictionary<string, ProcessInfo>(result.Count);
        foreach (var p in result)
        {
            if (grouped.TryGetValue(p.Name, out var acc))
            {
                acc.CpuPercent = Math.Round(acc.CpuPercent + p.CpuPercent, 1);
                acc.MemoryMb = Math.Round(acc.MemoryMb + p.MemoryMb, 1);
                acc.CpuTimeSeconds = Math.Round(acc.CpuTimeSeconds + p.CpuTimeSeconds, 1);
            }
            else
            {
                grouped[p.Name] = p;
            }
        }
        var sorted = new List<ProcessInfo>(grouped.Count);
        foreach (var v in grouped.Values)
        {
            sorted.Add(v);
        }
        sorted.Sort(static (a, b) =>
        {
            var c = b.CpuPercent.CompareTo(a.CpuPercent);
            return c != 0 ? c : b.MemoryMb.CompareTo(a.MemoryMb);
        });
        _latest = sorted;
    }
#endif

    /// <summary>
    /// Windows: Process.TotalProcessorTime works reliably via perf counters.
    /// Delta-based CPU% normalized by elapsed time and core count.
    /// </summary>
    private void SampleWindows()
    {
        var now = DateTime.UtcNow;
        var processes = Process.GetProcesses();
        var result = new List<ProcessInfo>();
        var seen = new HashSet<int>();

        foreach (var proc in processes)
        {
            try
            {
                var pid = proc.Id;
                seen.Add(pid);

                TimeSpan cpuTime;
                long memBytes;
                string name;
                try
                {
                    cpuTime = proc.TotalProcessorTime;
                    memBytes = proc.WorkingSet64;
                    name = proc.ProcessName;
                }
                catch { continue; }

                double cpuPercent = 0;
                if (_winPrev.TryGetValue(pid, out var prev))
                {
                    var elapsed = (now - prev.when).TotalMilliseconds;
                    if (elapsed > 50)
                    {
                        var delta = (cpuTime - prev.cpuTime).TotalMilliseconds;
                        cpuPercent = Math.Clamp(delta / elapsed / ProcessorCount * 100.0, 0, 100);
                    }
                }
                _winPrev[pid] = (cpuTime, now);

                // Running as LocalSystem denies StartTime for some processes
                // (elevated/protected system processes); leave it null rather
                // than dropping the whole entry.
                long? startedAtMs = null;
                try
                {
                    startedAtMs = new DateTimeOffset(proc.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
                }
                catch { }

                result.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    CpuPercent = Math.Round(cpuPercent, 1),
                    MemoryMb = Math.Round(memBytes / (1024.0 * 1024.0), 1),
                    CpuTimeSeconds = Math.Round(cpuTime.TotalSeconds, 1),
                    StartedAtMs = startedAtMs,
                });
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Prune stale PID entries.
        if (_winPrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_winPrev.Count - seen.Count);
            foreach (var k in _winPrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _winPrev.Remove(k);
            }
        }

        result.Sort(static (a, b) =>
        {
            var c = b.CpuPercent.CompareTo(a.CpuPercent);
            return c != 0 ? c : b.MemoryMb.CompareTo(a.MemoryMb);
        });
        _latest = result;
    }
}

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public double CpuTimeSeconds { get; set; }
    /// <summary>Process creation time, UTC epoch ms. Null when unavailable
    /// (access denied under LocalSystem, or not read on this platform).</summary>
    public long? StartedAtMs { get; set; }
}
