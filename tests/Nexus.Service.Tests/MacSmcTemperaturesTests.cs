using System.Collections.Generic;
using Nexus.Service.Platform.Mac;
using Xunit;

namespace Nexus.Service.Tests;

public class MacSmcTemperaturesTests
{
    [Fact]
    public void Average_ReturnsNullWhenNoKeyReads()
    {
        Assert.Null(MacSmcTemperatures.Average(_ => null, MacSmcTemperatures.CpuKeys));
    }

    [Fact]
    public void Average_IgnoresImplausibleReadings()
    {
        var values = new Dictionary<string, float?>
        {
            ["Tp01"] = 60f,
            ["Tp05"] = 70f,
            ["Tp09"] = 0f,
            ["Tp0D"] = -12f,
            ["Tp02"] = 200f,
        };
        var avg = MacSmcTemperatures.Average(
            k => values.TryGetValue(k, out var v) ? v : null,
            MacSmcTemperatures.CpuKeys);
        Assert.Equal(65f, avg);
    }

    [Fact]
    public void Average_ReadsExactlyTheRequestedKeys()
    {
        var reads = new List<string>();
        var avg = MacSmcTemperatures.Average(
            k => { reads.Add(k); return 50f; },
            MacSmcTemperatures.GpuKeys);
        Assert.Equal(50f, avg);
        Assert.Equal(MacSmcTemperatures.GpuKeys, reads);
    }
}
