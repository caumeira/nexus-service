using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

/// <summary>
/// Drives CoolingActions.RegisterAll's handlers directly through
/// AppActionRegistry against a hand-built IServiceProvider - the same shape
/// POST /apps-api/dispatch resolves from.
/// </summary>
public class CoolingActionsTests
{
    private sealed class RecordingFanControlProvider : IFanControlProvider
    {
        public (string Id, int Duty)? LastSetSpeed;
        public IReadOnlyList<FanChannel> GetFanChannels() => new[] { new FanChannel { Id = "fan-1", Name = "fan-1" } };
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => null;
        public int SetFanSpeed(string channelId, int dutyPercent) { LastSetSpeed = (channelId, dutyPercent); return dutyPercent; }
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }
        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private sealed class RecordingCurveProvider : ICurveProvider
    {
        public int SetCurvesCallCount { get; private set; }
        public void SetCurves(SetCurvesBody body) => SetCurvesCallCount++;
        public object? GetCalculatedById(string id) => null;
    }

    private static (AppActionRegistry Registry, IServiceProvider Services, InMemoryConfigStore Store, RecordingFanControlProvider Fans) Build(bool coolingEnabled)
    {
        var store = new InMemoryConfigStore();
        if (!coolingEnabled)
        {
            store.Update(s => s.Features.Cooling = false);
        }
        var fans = new RecordingFanControlProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IFanControlProvider>(fans);
        services.AddSingleton<IConfigStore>(store);
        services.AddSingleton<ICurveProvider>(new RecordingCurveProvider());
        services.AddSingleton<MultiplexHub>();
        services.AddSingleton<FeatureGates>();
        var sp = services.BuildServiceProvider();

        var registry = new AppActionRegistry();
        CoolingActions.RegisterAll(registry);
        return (registry, sp, store, fans);
    }

    private static async Task<JsonElement> Invoke(AppActionRegistry registry, IServiceProvider services, string action, Dictionary<string, JsonElement>? args)
    {
        Assert.True(registry.TryGet(action, out var handler));
        var result = await handler(services, args, CancellationToken.None);
        Assert.NotNull(result);
        return result!.Value;
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task SetDuty_GateOff_ReturnsDisabledAck_AndDoesNotSetSpeed()
    {
        var (registry, services, _, fans) = Build(coolingEnabled: false);

        var ack = await Invoke(registry, services, "cooling.setDuty", Args("""{"channelId":"fan-1","value":80}"""));

        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("cooling is disabled in Settings", ack.GetProperty("message").GetString());
        Assert.Null(fans.LastSetSpeed);
    }

    [Fact]
    public async Task SetDuty_GateOn_SetsSpeed()
    {
        var (registry, services, _, fans) = Build(coolingEnabled: true);

        var ack = await Invoke(registry, services, "cooling.setDuty", Args("""{"channelId":"fan-1","value":80}"""));

        Assert.True(ack.GetProperty("ok").GetBoolean());
        Assert.Equal(("fan-1", 80), fans.LastSetSpeed);
    }

    [Fact]
    public async Task ApplyPreset_GateOff_ReturnsDisabledAck_AndDoesNotApply()
    {
        var (registry, services, store, _) = Build(coolingEnabled: false);
        var before = store.Load().Cooling.ActivePreset;

        var ack = await Invoke(registry, services, "cooling.applyPreset", Args("""{"name":"silent"}"""));

        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("cooling is disabled in Settings", ack.GetProperty("message").GetString());
        Assert.Equal(before, store.Load().Cooling.ActivePreset);
    }

    [Fact]
    public async Task SetCurve_GateOff_ReturnsDisabledAck()
    {
        var (registry, services, _, _) = Build(coolingEnabled: false);

        var ack = await Invoke(registry, services, "cooling.setCurve",
            Args("""{"channelId":"fan-1","sourceId":"cpu-0","points":[{"temp":30,"speed":20},{"temp":80,"speed":100}]}"""));

        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("cooling is disabled in Settings", ack.GetProperty("message").GetString());
    }
}
