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

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = 80 });
        var fetched = _registry.Get(record.Id);

        Assert.Equal(80, patched!.WidgetPadding);
        Assert.Equal(80, fetched!.WidgetPadding);
    }

    [Fact]
    public void Patch_OmittedWidgetPadding_DoesNotClobberStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        _registry.Patch(record.Id, new PanelDevicePatch { WidgetPadding = 0 });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.Equal(0, patched!.WidgetPadding);
        Assert.Equal("Renamed", patched.DisplayName);
    }

    /// <summary>
    /// The service stores null until explicitly patched, same as WidgetOpacity/
    /// WidgetLabels; the default percent is applied client-side.
    /// </summary>
    [Fact]
    public void Allocate_WidgetPadding_AbsentIsNullNotServerDefaulted()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));

        Assert.Null(record.WidgetPadding);
    }

    [Fact]
    public void Patch_BackgroundEnabled_RoundTripsThroughGet()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundEnabled = false });
        var fetched = _registry.Get(record.Id);

        Assert.False(patched!.BackgroundEnabled);
        Assert.False(fetched!.BackgroundEnabled);
    }

    [Fact]
    public void Patch_OmittedBackgroundEnabled_DoesNotClobberStoredValue()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { BackgroundEnabled = false });

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });

        Assert.False(patched!.BackgroundEnabled);
    }

    [Fact]
    public void Patch_BackgroundFrost_RoundTripsAndEmptyClears()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Phone));
        Assert.Null(record.BackgroundFrost);

        var patched = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundFrost = "heavy" });
        Assert.Equal("heavy", patched!.BackgroundFrost);
        Assert.Equal("heavy", _registry.Get(record.Id)!.BackgroundFrost);

        var renamed = _registry.Patch(record.Id, new PanelDevicePatch { DisplayName = "Renamed" });
        Assert.Equal("heavy", renamed!.BackgroundFrost);

        var cleared = _registry.Patch(record.Id, new PanelDevicePatch { BackgroundFrost = "" });
        Assert.Null(cleared!.BackgroundFrost);
    }

    [Fact]
    public void ResetToDefaults_ClearsCustomizations_KeepsIdentity()
    {
        var record = _registry.Allocate("My Panel", Caps(PanelSurfaces.Phone));
        _registry.Patch(record.Id, new PanelDevicePatch
        {
            Layout = new PanelLayoutDto { Surface = PanelSurfaces.Phone },
            ThemeMode = "light",
            AccentColor = "#8b5cf6",
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
            BackgroundMode = "shader",
            BackgroundEffect = "plasma",
            BackgroundTemplate = 2,
            BackgroundTemplates = new Dictionary<string, int> { ["plasma"] = 2 },
            BackgroundOpacity = 0.3,
            BackgroundEnabled = false,
            BackgroundMediaId = "asset-1",
            BackgroundMediaType = "static",
            BackgroundFrost = "heavy",
            WidgetOpacity = 0.7,
            WidgetLabels = true,
            WidgetPadding = 25,
            ThemeSyncWithDesktop = false,
            AccentSyncWithDesktop = false,
        });

        var reset = _registry.ResetToDefaults(record.Id);

        Assert.NotNull(reset);
        Assert.Equal(record.Id, reset!.Id);
        Assert.Equal("My Panel", reset.DisplayName);
        Assert.Equal(record.FirstSeenAt, reset.FirstSeenAt);
        Assert.NotNull(reset.Capabilities);
        Assert.Null(reset.Layout);
        Assert.Null(reset.ThemeMode);
        Assert.Null(reset.AccentColor);
        Assert.Null(reset.BackgroundColor);
        Assert.Null(reset.BackgroundColorLight);
        Assert.Null(reset.BackgroundMode);
        Assert.Null(reset.BackgroundEffect);
        Assert.Null(reset.BackgroundTemplate);
        Assert.Null(reset.BackgroundTemplates);
        Assert.Null(reset.BackgroundOpacity);
        Assert.Null(reset.BackgroundEnabled);
        Assert.Null(reset.BackgroundMediaId);
        Assert.Null(reset.BackgroundMediaType);
        Assert.Null(reset.BackgroundFrost);
        Assert.Null(reset.WidgetOpacity);
        Assert.Null(reset.WidgetLabels);
        Assert.Null(reset.WidgetPadding);
        Assert.Null(reset.ThemeSyncWithDesktop);
        Assert.Null(reset.AccentSyncWithDesktop);
        Assert.Null(_registry.Get(record.Id)!.Layout);
    }

    [Fact]
    public void ResetToDefaults_DisplayBound_KeepsBindingEnabledAndMonitorSettings()
    {
        var (record, _) = _registry.AllocateForDisplay("DISPLAY-1", "Edge", Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { ReserveMonitor = false, AutoOrient = false, BackgroundFrost = "light" });

        var reset = _registry.ResetToDefaults(record.Id);

        Assert.NotNull(reset);
        Assert.Equal("DISPLAY-1", reset!.DisplayId);
        Assert.NotEqual(false, reset.Enabled);
        // Monitor behavior is hardware scope; personalization keeps it.
        Assert.False(reset.ReserveMonitor);
        Assert.False(reset.AutoOrient);
        Assert.Null(reset.BackgroundFrost);
    }

    [Fact]
    public void ResetHardwareSettings_ClearsMonitorBehavior_KeepsPersonalization()
    {
        var (record, _) = _registry.AllocateForDisplay("DISPLAY-1", "Edge", Caps(PanelSurfaces.Monitor));
        _registry.Patch(record.Id, new PanelDevicePatch { ReserveMonitor = false, AutoOrient = false, BackgroundFrost = "light" });
        _registry.UpdateXeneonEdgeSettings("DISPLAY-1", new XeneonEdgeSettingsDto { Brightness = 5 });

        var reset = _registry.ResetHardwareSettings(record.Id);

        Assert.NotNull(reset);
        Assert.Null(reset!.ReserveMonitor);
        Assert.Null(reset.AutoOrient);
        Assert.Null(reset.XeneonEdgeSettings);
        // Personalization is the other scope; hardware reset keeps it.
        Assert.Equal("light", reset.BackgroundFrost);
        Assert.Equal("DISPLAY-1", reset.DisplayId);
    }

    [Fact]
    public void ResetToDefaults_UnknownId_ReturnsNull()
    {
        Assert.Null(_registry.ResetToDefaults("nope"));
        Assert.Null(_registry.ResetHardwareSettings("nope"));
    }

    /// <summary>Null until explicitly patched; enabled is the client-side default.</summary>
    [Fact]
    public void Allocate_BackgroundEnabled_AbsentIsNullNotServerDefaulted()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        Assert.Null(record.BackgroundEnabled);
    }

    [Fact]
    public void UpdateXeneonEdgeSettings_PartialUpdate_DoesNotClobberOtherStoredControls()
    {
        var (display, _) = _registry.AllocateForDisplay("DISP-XENEON", "Xeneon Edge", Caps(PanelSurfaces.Monitor));
        _registry.UpdateXeneonEdgeSettings(display.DisplayId!, new XeneonEdgeSettingsDto { Brightness = 50, Red = 151 });

        _registry.UpdateXeneonEdgeSettings(display.DisplayId!, new XeneonEdgeSettingsDto { Brightness = 80 });

        var fetched = _registry.Get(display.Id);
        Assert.Equal(80, fetched!.XeneonEdgeSettings!.Brightness);
        Assert.Equal(151, fetched.XeneonEdgeSettings!.Red);
    }

    [Fact]
    public void ResolveCoverBackgroundHex_NullRecord_ReturnsEmpty()
    {
        Assert.Equal("", PanelDeviceRegistry.ResolveCoverBackgroundHex(null));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_NoColoursSet_ReturnsEmpty()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));

        Assert.Equal("", PanelDeviceRegistry.ResolveCoverBackgroundHex(record));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_DefaultThemeMode_PrefersTheDarkSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
        });

        Assert.Equal("#1f0d36", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_ExplicitLightThemeMode_PrefersTheLightSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            ThemeMode = "light",
            BackgroundColor = "#1f0d36",
            BackgroundColorLight = "#f3e8ff",
        });

        Assert.Equal("#f3e8ff", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }

    [Fact]
    public void ResolveCoverBackgroundHex_LightThemeModeButOnlyDarkSlotSet_FallsBackToTheDarkSlot()
    {
        var record = _registry.Allocate(null, Caps(PanelSurfaces.Monitor));
        var patched = _registry.Patch(record.Id, new PanelDevicePatch
        {
            ThemeMode = "light",
            BackgroundColor = "#1f0d36",
        });

        Assert.Equal("#1f0d36", PanelDeviceRegistry.ResolveCoverBackgroundHex(patched));
    }
}
