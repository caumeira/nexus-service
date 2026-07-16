using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// This is a singleton shared by every 1Hz caller (MonitoringBroadcaster,
/// MetricsSampler). Concurrent calls close together in time reuse the last
/// computed snapshot instead of each taking a delta off the same tick pair,
/// which would split one interval's ticks across two callers and corrupt
/// both percentages; the lock also keeps the tick-delta read/write atomic
/// against the torn-struct read two interleaved callers would otherwise risk.
/// </summary>
public sealed class MacPerformanceProvider : IPerformanceProvider
{
    private static readonly TimeSpan SampleFloor = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private MachStats.CpuLoadInfo _prevTicks;
    private bool _hasPrev;
    private long _totalMem = -1;
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

            double? cpu = null;
            if (MachStats.TryGetCpuLoad(out var ticks))
            {
                if (_hasPrev)
                {
                    uint du = ticks.UserTicks - _prevTicks.UserTicks;
                    uint ds = ticks.SystemTicks - _prevTicks.SystemTicks;
                    uint di = ticks.IdleTicks - _prevTicks.IdleTicks;
                    uint dn = ticks.NiceTicks - _prevTicks.NiceTicks;
                    uint dt = du + ds + di + dn;
                    cpu = dt > 0 ? Math.Clamp((du + ds) * 100.0 / dt, 0, 100) : 0;
                }
                _prevTicks = ticks;
                _hasPrev = true;
            }

            double? memory = null;
            if (MachStats.TryGetVmStats(out var vm))
            {
                long pageSize = MachStats.GetPageSize();
                var used = ((long)vm.ActivePages + vm.WiredPages + vm.CompressorPages) * pageSize;
                var total = GetTotalMemory();
                if (total > 0)
                    memory = Math.Clamp(used * 100.0 / total, 0, 100);
            }

            var snapshot = new PerformanceSnapshot
            {
                Cpu = cpu,
                Memory = memory,
                Gpu = null,
                Source = "macOS:mach",
            };
            _cached = snapshot;
            _cachedAtTicks = nowTicks;
            return Task.FromResult(snapshot);
        }
    }

    private long GetTotalMemory()
    {
        if (_totalMem >= 0)
            return _totalMem;
        _totalMem = MachStats.GetPhysicalMemory();
        return _totalMem;
    }
}
