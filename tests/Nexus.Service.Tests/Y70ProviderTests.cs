using System;
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
