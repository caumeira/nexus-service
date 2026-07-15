using System;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Single-instance surfaces (Y70, Q-series) must reuse one record across
/// (re)connects instead of accreting a fresh one - the Q-series OEM WebView
/// drops its cached deviceId, which otherwise mints a record every connect and
/// orphans the user's theme/layout.
/// </summary>
public sealed class PanelDeviceRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "nexus-paneldev-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;

    public PanelDeviceRegistryTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private static PanelDeviceCapabilities Caps(string surface) => new() { Surface = surface };

    [Theory]
    [InlineData(PanelSurfaces.Q60)]
    [InlineData(PanelSurfaces.Y70)]
    public void Allocate_SingleInstanceSurface_ReusesOneRecord(string surface)
    {
        var first = _registry.Allocate(null, Caps(surface));
        var second = _registry.Allocate(null, Caps(surface));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_registry.List());
    }

    [Fact]
    public void Allocate_Phone_DoesNotDedup()
    {
        var first = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        var second = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void Allocate_Reuse_PreservesThemeAndName()
    {
        var first = _registry.Allocate("My Q60", Caps(PanelSurfaces.Q60));
        _registry.Patch(first.Id, new PanelDevicePatch { BackgroundColor = "#78350f" });

        var reused = _registry.Allocate(null, Caps(PanelSurfaces.Q60));

        Assert.Equal(first.Id, reused.Id);
        Assert.Equal("My Q60", reused.DisplayName);
        Assert.Equal("#78350f", reused.BackgroundColor);
    }

    [Fact]
    public void Allocate_SingleInstance_DoesNotHijackDisplayBoundRecord()
    {
        var (display, _) = _registry.AllocateForDisplay("DISP-1", "Monitor", Caps(PanelSurfaces.Q60));

        var allocated = _registry.Allocate(null, Caps(PanelSurfaces.Q60));

        Assert.NotEqual(display.Id, allocated.Id);
        Assert.True(string.IsNullOrEmpty(allocated.DisplayId));
        Assert.Equal(2, _registry.List().Count);
    }

    [Fact]
    public void Patch_WidgetPadding_RoundTripsThroughGet()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = "large" });
        var fetched = _registry.Get(record.Id);

        Assert.Equal("large", patched!.WidgetPadding);
        Assert.Equal("large", fetched!.WidgetPadding);
    }

    [Fact]
    public void Patch_OmittedWidgetPadding_DoesNotClobberStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = "none" });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.Equal("none", patched!.WidgetPadding);
        Assert.Equal("Renamed", patched.DisplayName);
    }

    /// <summary>
    /// The service stores null until explicitly patched, same as WidgetOpacity/
    /// WidgetLabels/WidgetBlur; the "small" default is applied client-side.
    /// </summary>
    [Fact]
    public void Allocate_WidgetPadding_AbsentIsNullNotServerDefaulted()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        Assert.Null(record.WidgetPadding);
    }
}
