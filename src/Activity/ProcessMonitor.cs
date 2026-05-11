using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Qos.Service.Activity;

/// <summary>
/// Background service that samples running processes every ~2s and exposes the
/// latest snapshot via GetProcesses(). Only samples when at least one WebSocket
/// client is subscribed to process or monitoring topics.
/// </summary>
public sealed class ProcessMonitor : BackgroundService
{
    private static readonly int ProcessorCount = Environment.ProcessorCount;
    private volatile IReadOnlyList<ProcessInfo> _latest = Array.Empty<ProcessInfo>();
    private int _intervalMs = 1000;
    private readonly MultiplexHub _hub;

    // Delta tracking for CPU time (Windows uses TimeSpan, macOS uses nanoseconds).
    private readonly Dictionary<int, (TimeSpan cpuTime, DateTime when)> _winPrev = new();
    private readonly Dictionary<int, (ulong cpuNs, DateTime when)> _macPrev = new();

    public ProcessMonitor(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<ProcessInfo> GetProcesses() => _latest;

    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    private bool HasSubscribers =>
        _hub.TopicHasSubscribers("processes") || _hub.TopicHasSubscribers("monitoring");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasSubscribers)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        SampleMacOs();
                    else
                        SampleWindows();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[process-monitor] sample failed: {ex.Message}");
            }

            try
            { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>
    /// macOS: proc_pidinfo gives per-process CPU time and RSS via direct
    /// kernel syscalls — no subprocess spawn. Delta-based CPU% normalized
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

        // Prune stale PID entries without allocating a LINQ Where+ToList.
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

        // Group by process name in place: the first instance per name becomes
        // the accumulator so we don't double-allocate a ProcessInfo for every
        // unique name like the previous LINQ chain did.
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

                result.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    CpuPercent = Math.Round(cpuPercent, 1),
                    MemoryMb = Math.Round(memBytes / (1024.0 * 1024.0), 1),
                    CpuTimeSeconds = Math.Round(cpuTime.TotalSeconds, 1),
                });
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Clean stale PID entries without a LINQ Where+ToList roundtrip.
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

    private static string ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "";
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }
        catch { return ""; }
    }
}

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public double CpuTimeSeconds { get; set; }
}
