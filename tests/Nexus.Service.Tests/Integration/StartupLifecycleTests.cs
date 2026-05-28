using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Monitoring;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

internal sealed class StubSensorProvider : ISensorProvider
{
    public int CpuSensorReads { get; private set; }
    public int MemorySensorReads { get; private set; }
    public int ExtrasReads { get; private set; }

    public string GetCpuModel() => "test-cpu";
    public IReadOnlyList<HardwareSensor> GetCpuSensors()
    {
        CpuSensorReads++;
        return new[]
        {
            new HardwareSensor
            {
                Id = "cpu/load",
                Name = "CPU Total",
                Type = "Load",
                Value = 42,
                Units = "%",
                Formatted = "42%",
                Parent = new SensorParent { Id = "cpu", Name = "test-cpu" },
            },
        };
    }
    public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 20f);
    public IReadOnlyList<string> GetGpuModels() => Array.Empty<string>();
    public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
    public IReadOnlyList<HardwareSensor> GetMemorySensors()
    {
        MemorySensorReads++;
        return new[]
        {
            new HardwareSensor
            {
                Id = "mem/usage",
                Name = "Memory Usage",
                Type = "Load",
                Value = 30,
                Units = "%",
                Formatted = "30%",
                Parent = new SensorParent { Id = "memory", Name = "Memory" },
            },
        };
    }
    public string GetMemoryTotalFormatted() => "16 GB";
    public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents() =>
        new Dictionary<string, StorageComponent>();
    public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
    public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
    public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
    public string GetMotherboardModel() => "test-mobo";
    public SensorExtras GetSensorExtras()
    {
        ExtrasReads++;
        return new SensorExtras
        {
            Batteries =
            {
                new HardwareComponent
                {
                    Id = "battery/0",
                    Name = "Test Battery",
                    Sensors = new List<HardwareSensor>
                    {
                        new()
                        {
                            Id = "battery/0/charge",
                            Name = "Charge Level",
                            Type = "Level",
                            Value = 80,
                            Units = "%",
                            Formatted = "80%",
                            Parent = new SensorParent { Id = "battery/0", Name = "Test Battery" },
                        },
                    },
                },
            },
        };
    }
    public string GetOsVersion() => "test-os";
    public string GetRamBrandModel() => "";
    public string GetStorageBrandModel() => "";
    public void SetPollingRate(int pollingRate) { }
    public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StubPerformanceProvider : IPerformanceProvider
{
    public Task<PerformanceSnapshot> SampleAsync(CancellationToken ct = default) =>
        Task.FromResult(new PerformanceSnapshot { Cpu = 5.0, Memory = 30.0, Gpu = 2.0, Source = "test" });
}

internal sealed class TrackingFpsProvider : IFpsProvider
{
    public int StartTransitions { get; private set; }
    public int StopTransitions { get; private set; }
    public int ComponentReads { get; private set; }
    public bool Running { get; private set; }

    public void Start()
    {
        if (Running)
            return;

        Running = true;
        StartTransitions++;
    }

    public void Stop()
    {
        if (!Running)
            return;

        Running = false;
        StopTransitions++;
    }

    public HardwareComponent GetComponent()
    {
        ComponentReads++;
        return new HardwareComponent
        {
            Id = "fps",
            Name = "FPS",
            Sensors = new List<HardwareSensor>
            {
                new()
                {
                    Id = "fps/current",
                    Name = "FPS",
                    Type = "Framerate",
                    Value = 60,
                    Units = "fps",
                    Formatted = "60 fps",
                    Parent = new SensorParent { Id = "fps", Name = "FPS" },
                },
            },
        };
    }

    public void Dispose() => Stop();
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow += amount;
}

/// <summary>
/// End-to-end coverage of the monitoring broadcast path: a simulated subscriber
/// turns a topic "live", the broadcaster gathers data from its provider stack
/// (all stubs for test determinism), and assembles + fires a frame through
/// <see cref="MultiplexHub"/>. Catches regressions in:
///   - DI-injected provider contracts (a stub provider returning null / empty
///     in a new field would throw here before it ships)
///   - Topic-first-subscriber event wiring
///   - JSON serialisation of MonitoringFrame / ProcessFrame / NetworkFrame via
///     the AOT source-gen context (an unregistered type serialises as "{}"
///     silently in AOT; the test asserts a non-trivial payload)
///   - `Tick()` internal gating logic (an early-return regression would skip
///     the broadcast even with a subscriber)
///
/// The test runs purely in-process with stub providers -- no hardware, no
/// Kestrel, no sockets -- by using MultiplexHub's internal test-subscription
/// hook and OnBroadcastForTest event. That keeps the test to <50 ms while still
/// exercising the real class graph the broadcaster uses in production.
/// </summary>
public class StartupLifecycleTests
{
    private static MonitoringBroadcaster BuildBroadcaster(
        MultiplexHub hub,
        StubSensorProvider? sensors = null,
        TrackingFpsProvider? fps = null,
        TimeProvider? timeProvider = null)
    {
        // Stub providers for every dependency so Tick can run end-to-end
        // without touching hardware.
        sensors ??= new StubSensorProvider();
        fps ??= new TrackingFpsProvider();
        var network = new StubNetworkProvider();
        var screenTime = new StubScreenTimeProvider();
        var performance = new StubPerformanceProvider();
        var processes = new ProcessMonitor(hub);
        var volume = new StubVolumeProvider();
        return timeProvider is null
            ? new MonitoringBroadcaster(sensors, processes, network, performance, screenTime, volume, fps, hub)
            : new MonitoringBroadcaster(sensors, processes, network, performance, screenTime, volume, fps, hub, timeProvider);
    }

    [Fact]
    public async Task Tick_WithNoSubscribers_ShortCircuits_NoBroadcast()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        await broadcaster.Tick(CancellationToken.None);

        Assert.Empty(captured);
    }

    [Fact]
    public async Task Tick_WithMonitoringSubscriber_BroadcastsCompositeFrame()
    {
        var hub = new MultiplexHub();
        var fps = new TrackingFpsProvider();
        var broadcaster = BuildBroadcaster(hub, fps: fps);
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, payload.ToArray()));

        using var sub = hub.AddTestSubscription("monitoring");
        Assert.True(hub.TopicHasSubscribers("monitoring"));

        await broadcaster.Tick(CancellationToken.None);

        // Composite "monitoring" topic must have been broadcast and carry a
        // non-empty envelope with the expected wrapper shape.
        var monitoring = captured.FirstOrDefault(c => c.Topic == "monitoring");
        Assert.NotEqual(default, monitoring);
        var json = System.Text.Encoding.UTF8.GetString(monitoring.Payload);
        Assert.StartsWith("{\"t\":\"monitoring\"", json);
        Assert.Contains("\"d\":{", json);
        // AOT-safety guard: if MonitoringFrame's TypeInfo isn't registered in
        // AppJsonContext the payload becomes `"d":{}` and this assertion fails.
        Assert.DoesNotContain("\"d\":{}", json);
        Assert.DoesNotContain("fps", captured.Select(c => c.Topic));
        Assert.Equal(0, fps.StartTransitions);
        Assert.Equal(0, fps.ComponentReads);
    }

    [Fact]
    public async Task Tick_WithFpsSubscriber_StartsAndBroadcastsFpsTopicOnly()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var fps = new TrackingFpsProvider();
        var broadcaster = BuildBroadcaster(hub, sensors, fps);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using (var sub = hub.AddTestSubscription("fps"))
        {
            Assert.True(fps.Running);
            await broadcaster.Tick(CancellationToken.None);
        }

        Assert.Contains(captured, c => c.Topic == "fps" && c.Payload.Contains("\"FPS\""));
        Assert.DoesNotContain("monitoring", captured.Select(c => c.Topic));
        Assert.DoesNotContain("cpu", captured.Select(c => c.Topic));
        Assert.Equal(0, sensors.CpuSensorReads);
        Assert.Equal(1, fps.StartTransitions);
        Assert.Equal(1, fps.StopTransitions);
        Assert.Equal(1, fps.ComponentReads);
    }

    [Fact]
    public async Task Tick_WithCompositeAndSensorSubscribers_ReusesSensorPayload()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var monitoring = hub.AddTestSubscription("monitoring");
        using var cpu = hub.AddTestSubscription("cpu");
        using var memory = hub.AddTestSubscription("memory");

        await broadcaster.Tick(CancellationToken.None);

        Assert.Contains("monitoring", captured);
        Assert.Contains("cpu", captured);
        Assert.Contains("memory", captured);
        Assert.Equal(1, sensors.CpuSensorReads);
        Assert.Equal(1, sensors.MemorySensorReads);
    }

    [Fact]
    public async Task Tick_WithScreenTimeSubscriber_BroadcastsScreenTimeTopicOnly()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var sub = hub.AddTestSubscription("screentime");
        await broadcaster.Tick(CancellationToken.None);

        // screentime on its own must NOT trigger LHM sensor gathering or the
        // composite frame. Regression here wastes CPU on users who only want
        // focus tracking.
        Assert.Contains("screentime", captured);
        Assert.DoesNotContain("monitoring", captured);
        Assert.DoesNotContain("cpu", captured);
        Assert.DoesNotContain("processes", captured);
    }

    [Fact]
    public async Task Tick_WithScreenTimeSubscriber_ThrottlesScreenTimeToTenSeconds()
    {
        var hub = new MultiplexHub();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero));
        var broadcaster = BuildBroadcaster(hub, timeProvider: time);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        using var sub = hub.AddTestSubscription("screentime");

        await broadcaster.Tick(CancellationToken.None);
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(1, captured.Count(t => t == "screentime"));

        time.Advance(TimeSpan.FromSeconds(9));
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(1, captured.Count(t => t == "screentime"));

        time.Advance(TimeSpan.FromSeconds(1));
        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(2, captured.Count(t => t == "screentime"));
    }

    [Fact]
    public async Task Tick_SubscribeThenUnsubscribe_ReturnsToIdle()
    {
        var hub = new MultiplexHub();
        var broadcaster = BuildBroadcaster(hub);
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        var sub = hub.AddTestSubscription("monitoring");
        await broadcaster.Tick(CancellationToken.None);
        Assert.NotEmpty(captured);

        captured.Clear();
        sub.Dispose();

        Assert.False(hub.TopicHasSubscribers("monitoring"));
        await broadcaster.Tick(CancellationToken.None);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Tick_WithExtrasSubscriber_BroadcastsExtrasTopicOnly()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);
        var captured = new List<(string Topic, string Payload)>();
        hub.OnBroadcastForTest += (topic, payload) =>
            captured.Add((topic, System.Text.Encoding.UTF8.GetString(payload.Span)));

        using var sub = hub.AddTestSubscription("extras");
        await broadcaster.Tick(CancellationToken.None);

        // Extras must broadcast under its own topic only and never piggy-back
        // on the composite "monitoring" frame -- other pages must stay free of
        // the detailed-tab payload.
        var extras = captured.FirstOrDefault(c => c.Topic == "extras");
        Assert.NotEqual(default, extras);
        Assert.StartsWith("{\"t\":\"extras\"", extras.Payload);
        Assert.Contains("\"batteries\"", extras.Payload);
        Assert.DoesNotContain("monitoring", captured.Select(c => c.Topic));
        Assert.DoesNotContain("cpu", captured.Select(c => c.Topic));
        Assert.Equal(1, sensors.ExtrasReads);
        Assert.Equal(0, sensors.CpuSensorReads);
    }

    [Fact]
    public async Task Tick_WithoutExtrasSubscriber_DoesNotGatherExtras()
    {
        var hub = new MultiplexHub();
        var sensors = new StubSensorProvider();
        var broadcaster = BuildBroadcaster(hub, sensors);

        // Composite + each per-domain topic, but never "extras". The broadcaster
        // must still skip extras gathering -- this is the contract that keeps
        // the Overview/CPU/Memory/Network/ScreenTime tabs flood-free.
        using var monitoring = hub.AddTestSubscription("monitoring");
        using var cpu = hub.AddTestSubscription("cpu");

        await broadcaster.Tick(CancellationToken.None);

        Assert.Equal(0, sensors.ExtrasReads);
    }

    [Fact]
    public void AddTestSubscription_FiresFirstSubscriberEvent()
    {
        var hub = new MultiplexHub();
        var firstTopics = new List<string>();
        hub.OnTopicFirstSubscriber += t => firstTopics.Add(t);

        using var s1 = hub.AddTestSubscription("processes");
        using var s2 = hub.AddTestSubscription("processes"); // second sub, same topic
        using var s3 = hub.AddTestSubscription("network");

        // First-subscriber event must fire exactly once per topic transition
        // from 0 -> 1 subscriber. Regression here would cause BeatsProvider-
        // style lazy subsystems to start twice or never.
        Assert.Equal(new[] { "processes", "network" }, firstTopics);
    }
}
