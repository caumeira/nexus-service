using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Tests;
using Xunit;

namespace Nexus.Service.Tests.Np50;

/// <summary>
/// Representative of the 12 first-party frame writers: gate-off must skip
/// the write even with a connected hub and a populated device frame -
/// exactly the setup that DOES push bytes with the gate on, so the negative
/// result cannot be mistaken for "no hardware to write to".
/// </summary>
public class Np50LightingFrameWriterGateTests
{
    private static (Np50Hub Hub, FakeTransport Transport, LightingEngine Engine, Np50LightingFrameWriter Writer) Build(FeatureGates gates)
    {
        var transport = new FakeTransport();
        var hub = new Np50Hub(new FakeDiscovery(), _ => transport);
        Assert.True(hub.EnsureConnected());

        var engine = new LightingEngine();
        engine.UpdateDevices(new[] { new DeviceFrame(0, "np50:NPTEST:port1:dev0", ledCount: 3) });

        var store = new InMemoryConfigStore();
        var writer = new Np50LightingFrameWriter(engine, hub, store, new Np50IdentifyTracker(), gates);
        return (hub, transport, engine, writer);
    }

    [Fact]
    public void Tick_GateOff_WritesNothing_EvenWithAConnectedHubAndAPopulatedFrame()
    {
        var configStore = new InMemoryConfigStore();
        configStore.Update(s => s.Features.Lighting = false);
        var (_, transport, _, writer) = Build(new FeatureGates(configStore));

        writer.Tick();

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void Tick_GateOn_WritesTheLightingCycle_ForTheSameSetup()
    {
        var configStore = new InMemoryConfigStore();
        var (_, transport, _, writer) = Build(new FeatureGates(configStore));

        writer.Tick();

        // Sanity/positive control: the identical setup with the gate on
        // proves the negative result above is the gate, not a fixture gap.
        Assert.NotEmpty(transport.Writes);
    }

    [Fact]
    public void Tick_BlackoutEngagedWhileGateStillOn_DeliversTheFarewellFrame_ThenStaysZeroWritesOnceGateFlips()
    {
        // Hand-replays the sequence FeatureReconciler.ApplyPatch drives in
        // production (blackout while the gate still reads on, then the store
        // commit) to prove the writer's own response to it; the reconciler's
        // actual ordering is covered separately in FeatureReconcilerTests.
        var configStore = new InMemoryConfigStore();
        var (_, transport, engine, writer) = Build(new FeatureGates(configStore));

        engine.SetBlackout(true);
        Assert.True(engine.WaitForBlackout(TimeSpan.FromSeconds(1)));
        writer.Tick();
        Assert.NotEmpty(transport.Writes);

        transport.Writes.Clear();
        configStore.Update(s => s.Features.Lighting = false);
        writer.Tick();

        // Steady-state zero-writes holds even with the blackout hold still
        // engaged: the writer's gate check is unchanged, so it never re-reads
        // or re-pushes the held black frame on later ticks.
        Assert.Empty(transport.Writes);
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() =>
            new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = "NPTEST" } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool Disposed { get; private set; }

        public bool IsOpen => !Disposed;
        public string Serial => "NPTEST";
        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (Disposed) throw new IOException("port closed");
            Writes.Add(data.ToArray());
        }

        public int Read(Span<byte> buffer, int timeoutMs) => 0;

        public void Dispose() => Disposed = true;
    }
}
