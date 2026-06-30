using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class CpuClockAggregatesTests
{
    private static HardwareSensor CoreClock(string name, float mhz) => new()
    {
        Id = $"/intelcpu/0/clock/{name.ToLowerInvariant().Replace(" ", "-").Replace("#", "")}",
        Name = name,
        Type = "Clock",
        Value = mhz,
        Units = "MHz",
        Parent = new SensorParent { Id = "/intelcpu/0", Name = "Test CPU" },
    };

    [Theory]
    [InlineData(new float[] { 3600f, 3400f, 3200f }, 3600f, 3400f)]
    [InlineData(new float[] { 5000f, 4800f }, 5000f, 4900f)]
    [InlineData(new float[] { 2500f }, 2500f, 2500f)]
    public void Append_synthesizes_correct_max_and_average(
        float[] coreClocks, float expectedMax, float expectedAvg)
    {
        var mapped = new List<HardwareSensor>();
        for (var i = 0; i < coreClocks.Length; i++)
        {
            mapped.Add(CoreClock($"P-Core #{i + 1}", coreClocks[i]));
        }
        // These must be excluded from the aggregate.
        mapped.Add(CoreClock("Bus Speed", 100f));
        mapped.Add(new HardwareSensor { Id = "m", Name = "Memory", Type = "Clock", Value = 3600f });

        var result = new List<HardwareSensor>(mapped);
        CpuClockAggregates.Append("/intelcpu/0", "Test CPU", mapped, result);

        var coreMax = result.Find(s => s.Name == "Core Max");
        var coreAvg = result.Find(s => s.Name == "Core Average");

        Assert.NotNull(coreMax);
        Assert.NotNull(coreAvg);
        Assert.Equal("Clock", coreMax.Type);
        Assert.Equal("Clock", coreAvg.Type);
        Assert.Equal("MHz", coreMax.Units);
        Assert.Equal("MHz", coreAvg.Units);
        Assert.Equal("/intelcpu/0/clock/core-max", coreMax.Id);
        Assert.Equal("/intelcpu/0/clock/core-average", coreAvg.Id);
        Assert.Equal(expectedMax, coreMax.Value);
        Assert.Equal(expectedAvg, coreAvg.Value, 1);
    }

    [Fact]
    public void Append_excludes_bus_speed_from_aggregate()
    {
        var mapped = new List<HardwareSensor>
        {
            CoreClock("P-Core #1", 5000f),
            CoreClock("Bus Speed", 100f),
        };
        var result = new List<HardwareSensor>(mapped);
        CpuClockAggregates.Append("/intelcpu/0", "Test CPU", mapped, result);

        var coreMax = result.Find(s => s.Name == "Core Max");
        Assert.NotNull(coreMax);
        Assert.Equal(5000f, coreMax.Value);

        var coreAvg = result.Find(s => s.Name == "Core Average");
        Assert.NotNull(coreAvg);
        Assert.Equal(5000f, coreAvg.Value);
    }

    [Fact]
    public void Append_is_no_op_when_no_core_clock_sensors()
    {
        var mapped = new List<HardwareSensor>
        {
            CoreClock("Bus Speed", 100f),
            new() { Id = "m", Name = "Memory", Type = "Clock", Value = 3600f },
            new() { Id = "l", Name = "CPU Total", Type = "Load", Value = 50f },
        };
        var result = new List<HardwareSensor>(mapped);

        CpuClockAggregates.Append("/intelcpu/0", "Test CPU", mapped, result);

        Assert.Equal(mapped.Count, result.Count);
    }

    [Fact]
    public void Append_formatted_matches_clock_format()
    {
        var mapped = new List<HardwareSensor>
        {
            CoreClock("P-Core #1", 4800f),
            CoreClock("E-Core #1", 3600f),
        };
        var result = new List<HardwareSensor>(mapped);
        CpuClockAggregates.Append("/intelcpu/0", "Test CPU", mapped, result);

        var coreMax = result.Find(s => s.Name == "Core Max");
        var coreAvg = result.Find(s => s.Name == "Core Average");

        Assert.NotNull(coreMax);
        Assert.NotNull(coreAvg);
        Assert.Equal("4800 MHz", coreMax.Formatted);
        Assert.Equal("4200 MHz", coreAvg.Formatted);
    }
}
