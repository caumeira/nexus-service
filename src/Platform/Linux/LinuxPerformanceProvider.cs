using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Linux implementation of IPerformanceProvider. Reads CPU ticks from /proc/stat
/// and memory from /proc/meminfo - both pure-syscall reads, no subprocesses.
/// GPU sampling is deliberately skipped here (covered by LinuxSensorProvider via
/// nvidia-smi) because this provider is called on the monitoring hot path.
///
/// This is a singleton shared by every 1Hz caller (MonitoringBroadcaster,
/// MetricsSampler). Concurrent calls close together in time reuse the last
/// computed snapshot instead of each taking a delta off the same tick pair,
/// which would split one interval's ticks across two callers and corrupt
/// both percentages; the lock also keeps the tick-delta read/write atomic
/// against interleaved callers.
/// </summary>
public sealed class LinuxPerformanceProvider : IPerformanceProvider
{
    private static readonly TimeSpan SampleFloor = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _hasPrev;
    private PerformanceSnapshot? _cached;
    private long _cachedAtTicks = -1;

    public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var nowTicks = Environment.TickCount64;
            if (_cached is { } cached && _cachedAtTicks >= 0 && nowTicks - _cachedAtTicks < SampleFloor.TotalMilliseconds)
            {
                return Task.FromResult(cached);
            }

            var snapshot = new PerformanceSnapshot
            {
                Cpu = ReadCpuPercent(),
                Memory = ReadMemoryPercent(),
                Gpu = null,
                Source = "Linux:proc",
            };
            _cached = snapshot;
            _cachedAtTicks = nowTicks;
            return Task.FromResult(snapshot);
        }
    }

    private double? ReadCpuPercent()
    {
        try
        {
            var line = ReadFirstLine("/proc/stat");
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                return null;
            }

            ulong user = ParseUlong(parts, 1);
            ulong nice = ParseUlong(parts, 2);
            ulong system = ParseUlong(parts, 3);
            ulong idle = ParseUlong(parts, 4);
            ulong iowait = parts.Length > 5 ? ParseUlong(parts, 5) : 0;
            ulong irq = parts.Length > 6 ? ParseUlong(parts, 6) : 0;
            ulong softirq = parts.Length > 7 ? ParseUlong(parts, 7) : 0;
            ulong steal = parts.Length > 8 ? ParseUlong(parts, 8) : 0;

            ulong idleAll = idle + iowait;
            ulong total = user + nice + system + idleAll + irq + softirq + steal;

            double? result = null;
            if (_hasPrev)
            {
                ulong dt = total - _prevTotal;
                ulong di = idleAll - _prevIdle;
                if (dt > 0)
                {
                    result = Math.Clamp((dt - di) * 100.0 / dt, 0, 100);
                }
            }

            _prevIdle = idleAll;
            _prevTotal = total;
            _hasPrev = true;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadMemoryPercent()
    {
        try
        {
            long total = 0;
            long available = 0;
            foreach (var raw in File.ReadAllLines("/proc/meminfo"))
            {
                if (raw.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemLineKb(raw);
                }
                else if (raw.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemLineKb(raw);
                }

                if (total > 0 && available > 0)
                {
                    break;
                }
            }

            if (total <= 0)
            {
                return null;
            }

            long used = total - available;
            return Math.Clamp(used * 100.0 / total, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private static long ParseMemLineKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }
        return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : 0;
    }

    private static ulong ParseUlong(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }
        return ulong.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var r = new StreamReader(path);
            return r.ReadLine();
        }
        catch
        {
            return null;
        }
    }
}
