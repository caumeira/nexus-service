using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class FleetEventServiceTests
{
    private sealed class FakeFleetEventTransport : IFleetEventTransport
    {
        public List<FleetEventPayload> Sent { get; } = new();
        public Func<FleetEventPayload, bool> Respond { get; set; } = _ => true;

        public Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.FromResult(Respond(payload));
        }
    }

    private sealed class FakeTelemetrySink : ITelemetrySink
    {
        public bool Enabled { get; set; } = true;
        public List<TelemetryEvent> Received { get; } = new();

        public Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct)
        {
            Received.AddRange(batch);
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal ISensorProvider double; every non-configured member
    /// returns an empty placeholder, matching the real providers' contract.</summary>
    private sealed class StubSensors : ISensorProvider
    {
        public string Cpu { get; init; } = "";
        public IReadOnlyList<string> GpuModels { get; init; } = Array.Empty<string>();
        public string MemoryFormatted { get; init; } = "";
        public string Motherboard { get; init; } = "";

        public string GetCpuModel() => Cpu;
        public IReadOnlyList<HardwareSensor> GetCpuSensors() => Array.Empty<HardwareSensor>();
        public (bool Healthy, float DistanceToTJMax) GetCpuHealth() => (true, 0f);
        public IReadOnlyList<string> GetGpuModels() => GpuModels;
        public IReadOnlyList<HardwareSensor> GetGpuSensors() => Array.Empty<HardwareSensor>();
        public IReadOnlyList<GpuReadout> GetGpus() => Array.Empty<GpuReadout>();
        public IReadOnlyList<HardwareSensor> GetMemorySensors() => Array.Empty<HardwareSensor>();
        public string GetMemoryTotalFormatted() => MemoryFormatted;
        public string GetRamBrandModel() => "";
        public IReadOnlyDictionary<string, StorageComponent> GetStorageComponents(bool includeSmart = true) => new Dictionary<string, StorageComponent>();
        public IReadOnlyList<string> GetStoragePartitions() => Array.Empty<string>();
        public IReadOnlyList<StorageDriveInfo> GetStorageInfo() => Array.Empty<StorageDriveInfo>();
        public string GetStorageBrandModel() => "";
        public IReadOnlyList<HardwareSensor> GetMotherboardSensors() => Array.Empty<HardwareSensor>();
        public string GetMotherboardModel() => Motherboard;
        public SensorExtras GetSensorExtras() => new();
        public string GetOsVersion() => "TestOS";
        public void SetPollingRate(int pollingRate) { }
        public Task ReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static FleetEventService MakeService(
        InMemoryConfigStore store,
        FakeFleetEventTransport transport,
        ITelemetry? telemetry = null,
        IEnumerable<ITelemetrySink>? sinks = null,
        StubSensors? sensors = null) =>
        new(store, transport, telemetry ?? new TelemetryClient(store),
            sinks ?? Array.Empty<ITelemetrySink>(), new SystemSpecsCollector(sensors ?? new StubSensors()));

    private static InMemoryConfigStore OptedInStore(bool installDelivered = true)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true;
            s.Telemetry.FleetInstallDelivered = installDelivered;
        });
        return store;
    }

    [Fact]
    public async Task Install_event_delivered_once_and_not_resent()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.True(store.Load().Telemetry.FleetInstallDelivered);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install); // not resent
    }

    [Fact]
    public async Task Install_event_stays_pending_until_transport_succeeds()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);
        var transport = new FakeFleetEventTransport { Respond = _ => false };
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.False(store.Load().Telemetry.FleetInstallDelivered);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Install);

        transport.Respond = _ => true; // the server recovers
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.True(store.Load().Telemetry.FleetInstallDelivered);

        var attemptsAfterDelivered = transport.Sent.Count(p => p.Type == TelemetryEvents.Install);
        await svc.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Equal(attemptsAfterDelivered, transport.Sent.Count(p => p.Type == TelemetryEvents.Install));
    }

    [Fact]
    public async Task Specs_event_skipped_when_hardware_inventory_not_ready()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, sensors: new StubSensors());

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.DoesNotContain(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        Assert.Equal("", store.Load().Telemetry.FleetSpecsHash);
    }

    [Fact]
    public async Task Specs_event_sent_once_then_skipped_until_the_hash_changes()
    {
        var store = OptedInStore();
        var transport = new FakeFleetEventTransport();
        var cpuA = new StubSensors { Cpu = "CPU A", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };

        var svc1 = MakeService(store, transport, sensors: cpuA);
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);
        var hash1 = store.Load().Telemetry.FleetSpecsHash;
        Assert.NotEqual("", hash1);

        // Same service, same snapshot again - unchanged hash, no resend.
        await svc1.RunPendingRetriesAsync(CancellationToken.None);
        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.Specs);

        // Next boot with different hardware - hash differs, resend.
        var cpuB = new StubSensors { Cpu = "CPU B", GpuModels = new[] { "GPU A" }, MemoryFormatted = "32 GB", Motherboard = "Board A" };
        var svc2 = MakeService(store, transport, sensors: cpuB);
        await svc2.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Equal(2, transport.Sent.Count(p => p.Type == TelemetryEvents.Specs));
        Assert.NotEqual(hash1, store.Load().Telemetry.FleetSpecsHash);

        var specsPayload = transport.Sent.Last(p => p.Type == TelemetryEvents.Specs).Specs;
        Assert.NotNull(specsPayload);
        Assert.Equal("CPU B", specsPayload!.Cpu);
        Assert.Equal(32L * 1024 * 1024 * 1024, specsPayload.RamBytes);
        Assert.Equal("Board A", specsPayload.Motherboard);
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_opt_out_bypasses_capture_and_hits_the_sink_directly()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false; // already flipped by the route before this call
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var telemetry = new TelemetryClient(store); // _enabled=false: Capture() would no-op
        var sink = new FakeTelemetrySink();
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, telemetry, new ITelemetrySink[] { sink });

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.OptOut && p.InstallId == "install-1");
        Assert.Single(sink.Received, e => e.Name == TelemetryEvents.OptOut);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent); // cleared on success
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_opt_in_uses_the_normal_capture_pipeline()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true; // already flipped by the route before this call
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptIn;
        });
        var telemetry = new TelemetryClient(store);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport, telemetry);

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptIn, CancellationToken.None);

        Assert.Single(transport.Sent, p => p.Type == TelemetryEvents.OptIn);
        Assert.Contains(telemetry.DrainBatch(10), e => e.Name == TelemetryEvents.OptIn);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task DeliverConsentTransitionAsync_with_no_install_id_clears_the_marker_without_sending()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut);
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.DeliverConsentTransitionAsync(TelemetryEvents.OptOut, CancellationToken.None);

        Assert.Empty(transport.Sent);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
    }

    [Fact]
    public async Task RunPendingRetriesAsync_retries_a_pending_opt_out_while_opted_out_but_nothing_else()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "install-1";
            s.Telemetry.FleetPendingConsentEvent = TelemetryEvents.OptOut;
        });
        var transport = new FakeFleetEventTransport();
        var svc = MakeService(store, transport);

        await svc.RunPendingRetriesAsync(CancellationToken.None);

        Assert.Single(transport.Sent); // only the opt_out retry - no install/specs traffic while opted out.
        Assert.Equal(TelemetryEvents.OptOut, transport.Sent[0].Type);
        Assert.Equal("", store.Load().Telemetry.FleetPendingConsentEvent);
        Assert.False(store.Load().Telemetry.FleetInstallDelivered);
    }
}
