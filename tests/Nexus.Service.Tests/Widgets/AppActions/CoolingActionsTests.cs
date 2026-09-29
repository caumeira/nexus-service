using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

/// <summary>Apps get only the cooling read action; every write stays unregistered.</summary>
public class CoolingActionsTests
{
    private sealed class StubFanControlProvider : IFanControlProvider
    {
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { new FanChannel { Id = "fan-1", Name = "fan-1" } };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) => throw new InvalidOperationException("apps must not write cooling");
        public void DriveFanSpeed(string channelId, int dutyPercent) => throw new InvalidOperationException("apps must not write cooling");
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static AppActionRegistry Registry()
    {
        var registry = new AppActionRegistry();
        CoolingActions.RegisterAll(registry);
        return registry;
    }

    [Fact]
    public async Task State_ReturnsChannels()
    {
        var services = new ServiceCollection()
            .AddSingleton<IFanControlProvider>(new StubFanControlProvider())
            .BuildServiceProvider();
        Assert.True(Registry().TryGet("cooling.state", out var handler));

        var result = await handler(services, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("fan-1", result!.Value.GetProperty("channels")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("cooling.setDuty")]
    [InlineData("cooling.applyPreset")]
    [InlineData("cooling.setCurve")]
    public void Writes_AreNotRegistered(string action)
    {
        Assert.False(Registry().TryGet(action, out _));
        Assert.DoesNotContain(action, CoolingActions.AllActions);
    }
}
