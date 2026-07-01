using Nexus.Service.Devices;
using Xunit;

namespace Nexus.Service.Tests;

public class DeviceControlGateTests
{
    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.True(gate.IsEnabled("cnvs"));
    }

    [Fact]
    public void SetEnabled_False_PersistsToDisabledList()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("cnvs", false);

        Assert.False(gate.IsEnabled("cnvs"));
        Assert.Contains("cnvs", store.Load().Devices.NexusControlDisabled);
    }

    [Fact]
    public void SetEnabled_True_RemovesFromDisabledList()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("cnvs", false);

        gate.SetEnabled("cnvs", true);

        Assert.True(gate.IsEnabled("cnvs"));
        Assert.DoesNotContain("cnvs", store.Load().Devices.NexusControlDisabled);
    }

    [Theory]
    [InlineData("CNVS")]
    [InlineData("Cnvs")]
    [InlineData("cnvs")]
    public void IsEnabled_IsCaseInsensitive(string lookupId)
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("cnvs", false);

        Assert.False(gate.IsEnabled(lookupId));
    }

    [Fact]
    public void SetEnabled_DoesNotDuplicateEntries()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("cnvs", false);
        gate.SetEnabled("CNVS", false);

        Assert.Single(store.Load().Devices.NexusControlDisabled);
    }
}
