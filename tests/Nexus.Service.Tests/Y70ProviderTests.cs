using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Effective-orientation logic for the Y70: ForceOrientation always drives the
/// hardware to PortraitFlipped regardless of the stored preference, the
/// stored preference applies unchanged when the flag is off, and SetOrientation
/// / SetForceOrientation persist only (the caller applies once via
/// ApplyEffectiveOrientation, so a combined update never double-writes).
/// </summary>
public class Y70ProviderTests
{
    private static Y70Provider Build(InMemoryY70ConfigStore store, FakeY70OrientationProvider orientation)
        => new(
            new Y70DisplayHub(
                new StubY70DisplayPortDiscovery(),
                _ => throw new InvalidOperationException("Y70 transport is not expected in these tests")),
            store,
            orientation,
            new StubDisplayBrightnessProvider());

    [Fact]
    public void SetOrientation_persists_without_applying_to_hardware()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");

        Assert.Equal("Landscape", provider.GetOrientation());
        Assert.Null(orientation.LastApplied);
    }

    [Fact]
    public void SetForceOrientation_persists_without_applying_to_hardware()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetForceOrientation(true);

        Assert.True(provider.GetForceOrientation());
        Assert.Null(orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_uses_stored_preference_when_force_is_off()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(false);
        provider.ApplyEffectiveOrientation();

        Assert.Equal("Landscape", orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_forces_PortraitFlipped_when_force_is_on()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(true);
        provider.ApplyEffectiveOrientation();

        Assert.Equal("Landscape", provider.GetOrientation());
        Assert.Equal("PortraitFlipped", orientation.LastApplied);
    }

    [Fact]
    public void ApplyEffectiveOrientation_reflects_current_force_flag()
    {
        var store = new InMemoryY70ConfigStore();
        store.Load().Y70.Orientation = "Portrait";
        store.Load().Y70.ForceOrientation = true;
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        provider.ApplyEffectiveOrientation();

        Assert.Equal("PortraitFlipped", orientation.LastApplied);
    }

    [Fact]
    public void Combined_update_applies_exactly_once()
    {
        var store = new InMemoryY70ConfigStore();
        var orientation = new FakeY70OrientationProvider();
        var provider = Build(store, orientation);

        // Mirrors the POST /y70/rotation route: persist whichever fields are
        // present, then apply once.
        provider.SetOrientation("Landscape");
        provider.SetForceOrientation(false);
        provider.ApplyEffectiveOrientation();

        Assert.Equal(1, orientation.ApplyCount);
        Assert.Equal("Landscape", orientation.LastApplied);
    }

    // ── Brightness/power transport routing ──
    // A Truly exposes the same CDC port as the serial models (the shared
    // FF DD version query answers, so the hub connects) but takes display
    // control only via DDC/CI; a connected hub must not select serial for it.

    private static (Y70Provider Provider, FakeNp50Transport Serial, FakeDdcBrightnessProvider Ddc, InMemoryY70ConfigStore Store)
        BuildConnected(string variant)
    {
        var transport = new FakeNp50Transport();
        var hub = new Y70DisplayHub(new FakeY70PortDiscovery(variant), _ => transport);
        Assert.True(hub.EnsureConnected());
        var ddc = new FakeDdcBrightnessProvider();
        var store = new InMemoryY70ConfigStore();
        var provider = new Y70Provider(hub, store, new FakeY70OrientationProvider(), ddc);
        return (provider, transport, ddc, store);
    }

    [Fact]
    public void SetBrightness_on_serial_variant_writes_serial_frame_only()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantInfinite);

        provider.SetBrightness(50);

        var frame = Assert.Single(serial.Writes);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(true, 50), frame);
        Assert.Empty(ddc.VcpWrites);
    }

    [Fact]
    public void SetBrightness_on_truly_uses_ddc_even_with_serial_connected()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(50);

        Assert.Empty(serial.Writes);
        // Reference Y70TouchDdcciDeviceBase writes brightness alone - no
        // power write rides along with a brightness change.
        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 50), write);
    }

    [Fact]
    public void SetToggle_off_on_truly_writes_standby_and_skips_brightness()
    {
        var (provider, serial, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetToggle(true);

        Assert.Empty(serial.Writes);
        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerStandby), write);
        Assert.True(provider.GetToggle());
    }

    [Fact]
    public void SetToggle_on_on_truly_writes_power_on_only()
    {
        var (provider, _, ddc, store) = BuildConnected(Y70DisplayProtocol.VariantTruly);
        store.Load().Y70.ScreenOff = true;

        provider.SetToggle(false);

        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpPower, Y70DisplayProtocol.VcpPowerOn), write);
        Assert.False(provider.GetToggle());
    }

    [Fact]
    public void SetBrightness_on_truly_writes_raw_percent_without_serial_floor()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(5);

        var write = Assert.Single(ddc.VcpWrites);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 5), write);
        Assert.Equal(5, provider.GetBrightness());
    }

    [Fact]
    public void SetBrightness_on_serial_variant_floors_to_firmware_minimum()
    {
        var (provider, serial, _, _) = BuildConnected(Y70DisplayProtocol.VariantInfinite);

        provider.SetBrightness(5);

        var frame = Assert.Single(serial.Writes);
        Assert.Equal(Y70DisplayProtocol.BuildSetBrightnessPower(true, Y70DisplayProtocol.MinBrightnessOnPercent), frame);
        Assert.Equal(5, provider.GetBrightness());
    }

    [Fact]
    public void Rapid_truly_brightness_sets_coalesce_to_latest_value()
    {
        var (provider, _, ddc, _) = BuildConnected(Y70DisplayProtocol.VariantTruly);

        provider.SetBrightness(30);
        provider.SetBrightness(60);

        // First write is immediate; the in-window second collapses to a
        // trailing write of the latest value (reference debounce, 100ms).
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 30), ddc.VcpWrites[0]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ddc.VcpWrites.Count < 2 && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(10);
        }
        Assert.Equal(2, ddc.VcpWrites.Count);
        Assert.Equal((FakeDdcBrightnessProvider.Id, Y70DisplayProtocol.VcpBrightness, 60), ddc.VcpWrites[1]);
        Assert.Equal(60, provider.GetBrightness());
    }

    [Fact]
    public void SetBrightness_on_truly_without_ddc_display_persists_store_only()
    {
        var (provider, serial, ddc, store) = BuildConnected(Y70DisplayProtocol.VariantTruly);
        ddc.DisplayId = null;

        provider.SetBrightness(70);

        Assert.Empty(serial.Writes);
        Assert.Empty(ddc.VcpWrites);
        Assert.Equal(70, store.Load().Y70.Brightness);
    }

    private sealed class FakeY70PortDiscovery : IY70DisplayPortDiscovery
    {
        private readonly string _variant;
        public FakeY70PortDiscovery(string variant) => _variant = variant;
        public IReadOnlyList<Y70DisplayPort> Discover() =>
            new[] { new Y70DisplayPort { PortName = "COM9", Serial = "TESTSER", Variant = _variant } };
    }

    private sealed class FakeNp50Transport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool IsOpen => true;
        public string Serial => "TESTSER";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class FakeDdcBrightnessProvider : IDisplayBrightnessProvider
    {
        public const string Id = "display-rtk409a";
        public string? DisplayId = Id;
        // The provider's trailing coalesced write lands from a timer thread;
        // snapshot under a lock so test asserts never race an Add.
        private readonly object _lock = new();
        private readonly List<(string Id, byte Code, int Value)> _vcpWrites = new();

        public IReadOnlyList<(string Id, byte Code, int Value)> VcpWrites
        {
            get { lock (_lock) return _vcpWrites.ToArray(); }
        }

        public string Hint => "";
        public IReadOnlyList<DisplayDto> Enumerate() => Array.Empty<DisplayDto>();
        public int? GetBrightness(string id) => null;
        public DisplayBrightnessDto SetBrightness(string id, int percent) => new() { Id = id };
        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new();
        public DisplayVcpDto? GetVcp(string id, byte code) => null;
        public bool SetVcp(string id, byte code, int value)
        {
            lock (_lock) _vcpWrites.Add((id, code, value));
            return true;
        }
        public string? FindDisplayIdByHardwareName(IReadOnlyList<string> nameFragments) => DisplayId;
    }

    private sealed class FakeY70OrientationProvider : IDisplayOrientationProvider
    {
        public string? LastApplied;
        public int ApplyCount;

        public (bool Ok, string Error) SetY70Orientation(string orientation)
        {
            LastApplied = orientation;
            ApplyCount++;
            return (true, "");
        }

        public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation) => (true, "");
    }

    /// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
    private sealed class InMemoryY70ConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
