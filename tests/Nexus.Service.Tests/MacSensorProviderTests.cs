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
}
