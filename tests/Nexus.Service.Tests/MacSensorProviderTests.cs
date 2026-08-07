using System.Linq;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests;

public class MacSensorProviderTests
{
    // CI macOS VMs expose no AppleSMC service; the provider must omit the
    // temperature sensors there instead of emitting a zero reading. On real
    // Macs this exercises the live SMC end to end. The expectation derives
    // from the provider's own SMC connection and cache (ReadCpuDieTemperature
    // / ReadGpuDieTemperature), so an asymmetric open failure cannot produce
    // a false red.
    [MacOnlyFact]
    public void EmitsDieTemperatures_WhenSmcReads()
    {
        using var provider = new MacSensorProvider();

        var cpuTemp = provider.GetCpuSensors().FirstOrDefault(s => s.Type == "Temperature");
        if (provider.ReadCpuDieTemperature() is not null)
        {
            Assert.NotNull(cpuTemp);
            Assert.InRange(cpuTemp!.Value, 1f, 149f);
            Assert.Equal("°C", cpuTemp.Units);
            Assert.Equal("cpu/temp", cpuTemp.Id);
        }
        else
        {
            Assert.Null(cpuTemp);
        }

        var gpuTemp = provider.GetGpuSensors().FirstOrDefault(s => s.Type == "Temperature");
        if (provider.ReadGpuDieTemperature() is not null &&
            provider.GetGpuModels().Count > 0)
        {
            Assert.NotNull(gpuTemp);
            Assert.InRange(gpuTemp!.Value, 1f, 149f);
        }
        else
        {
            Assert.Null(gpuTemp);
        }
    }

    // Same iff pattern as the temperatures: asserted present only when the
    // provider's own IOAccelerator read yields a value (absent on CI VMs
    // with no GPU acceleration).
    [MacOnlyFact]
    public void EmitsGpuUtilization_WhenAcceleratorReads()
    {
        using var provider = new MacSensorProvider();

        var load = provider.GetGpuSensors().FirstOrDefault(s => s.Type == "Load");
        if (provider.ReadGpuUtilization() is not null && provider.GetGpuModels().Count > 0)
        {
            Assert.NotNull(load);
            Assert.InRange(load!.Value, 0f, 100f);
            Assert.Equal("GPU Core", load.Name);
        }
        else
        {
            Assert.Null(load);
        }
    }

    [MacOnlyFact]
    public void EmitsDriveTemperature_WhenNandKeysRead()
    {
        using var provider = new MacSensorProvider();

        var components = provider.GetStorageComponents(includeSmart: false);
        var driveTemps = components.Values
            .SelectMany(c => c.Sensors)
            .Where(s => s.Type == "Temperature")
            .ToList();
        if (provider.ReadSsdTemperature() is not null &&
            components.Keys.Any(k => k is "/" or "/System/Volumes/Data"))
        {
            var temp = Assert.Single(driveTemps);
            Assert.InRange(temp.Value, 1f, 149f);
            Assert.Equal("Drive Temperature", temp.Name);
        }
        else
        {
            Assert.Empty(driveTemps);
        }
    }

    // Two immediate reads land inside one poll-cache window, so every load
    // value must be identical - the pre-cache implementation advanced the
    // tick delta per call and would return drifting values.
    [MacOnlyFact]
    public void CpuLoadIsStableWithinOneCacheWindow()
    {
        using var provider = new MacSensorProvider();

        // Warm the lazily cached shell-outs (cpu model, core counts) so the
        // timed pair below stays well inside one cache window.
        provider.GetCpuSensors();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = provider.GetCpuSensors().Where(s => s.Type == "Load").ToList();
        var second = provider.GetCpuSensors().Where(s => s.Type == "Load").ToList();
        sw.Stop();
        if (sw.ElapsedMilliseconds >= MacSensorProvider.PollCacheTtlMs * 9 / 10)
        {
            return; // box too loaded to keep both reads in one window
        }

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Id, second[i].Id);
            Assert.Equal(first[i].Value, second[i].Value);
        }
    }
}
