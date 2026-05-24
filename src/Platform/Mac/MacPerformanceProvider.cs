using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Mac;

public sealed class MacPerformanceProvider : IPerformanceProvider
{
    private MachStats.CpuLoadInfo _prevTicks;
    private bool _hasPrev;
    private long _totalMem = -1;

    public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default)
    {
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

        return Task.FromResult(new PerformanceSnapshot
        {
            Cpu = cpu,
            Memory = memory,
            Gpu = null,
            Source = "macOS:mach",
        });
    }

    private long GetTotalMemory()
    {
        if (_totalMem >= 0)
            return _totalMem;
        _totalMem = MachStats.GetPhysicalMemory();
        return _totalMem;
    }
}
