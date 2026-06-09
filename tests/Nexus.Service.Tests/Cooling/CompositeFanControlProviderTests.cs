using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class CompositeFanControlProviderTests
{
    // Regression guard: Extras() must iterate _extras (the platform sources), NOT
    // itself. A self-call recursed infinitely and stack-overflowed the live app on
    // the first GetFanChannels — invisible to other tests because they never build
    // and enumerate the real composite.
    [Fact]
    public void GetFanChannels_aggregates_all_sources_without_recursing()
    {
        var noPorts = new NoPorts();
        var np50 = new Np50CoolingProvider(new Np50Hub(noPorts, _ => null!));
        var miniHub = new MiniHubCoolingProvider(new MiniHubHub(noPorts, _ => null!));

        var registry = new PluginProviderRegistry();
        registry.Add(new RegisteredProvider("aaa", "plugin:aaa:", CapabilityGrant.FirstParty("aaa"),
            Fans: new FakeFans("plugin:aaa:fan0")));

        var composite = new CompositeFanControlProvider(
            new FakeFans("mb:fan0"), np50, miniHub, registry,
            new CompositeFanControlProvider.FanSource(
                id => id.StartsWith("ext:", StringComparison.Ordinal), new FakeFans("ext:fan0")));

        var ids = composite.GetFanChannels().Select(c => c.Id).ToList();

        Assert.Contains("mb:fan0", ids);          // motherboard
        Assert.Contains("ext:fan0", ids);         // platform extra (_extras)
        Assert.Contains("plugin:aaa:fan0", ids);  // registry plugin source
    }

    private sealed class NoPorts : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() => Array.Empty<Np50PortInfo>();
    }

    private sealed class FakeFans : IFanControlProvider
    {
        private readonly string _id;
        public FakeFans(string id) => _id = id;
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { new FanChannel { Id = _id, Name = _id } };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }
}
