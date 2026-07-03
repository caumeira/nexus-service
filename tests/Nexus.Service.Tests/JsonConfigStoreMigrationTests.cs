using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Verifies JsonConfigStore is a no-op on records already at the current
/// schema version. The legacy V1-V4 migration tests were removed alongside
/// the migration code itself (nexus-service drop-legacy-schema-migrations
/// refactor).
/// </summary>
public class JsonConfigStoreMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public JsonConfigStoreMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoOpOnAlreadyV5()
    {
        var v5Json = """
        {
          "schemaVersion": 5,
          "theme": { "themeMode": "dark", "accentColor": "#aabbcc", "language": "en" },
          "panel": { "autoLaunch": true, "themeMode": "system" },
          "overlay": { "enabled": true, "scale": 120 },
          "monitoring": { "showAverage": true }
        }
        """;
        File.WriteAllText(_settingsPath, v5Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("dark", s.Theme.ThemeMode);
            Assert.Equal("#aabbcc", s.Theme.AccentColor);
            Assert.True(s.Panel.AutoLaunch);
            Assert.True(s.Overlay.Enabled);
            Assert.Equal(120, s.Overlay.Scale);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Theory]
    [InlineData(true, "notify")]
    [InlineData(false, "always")]
    public void Load_V6_MigratesLegacyAutoUpdateDisabled(bool legacyDisabled, string expectedMode)
    {
        var json = $$"""
        {
          "schemaVersion": 6,
          "update": { "autoUpdateDisabled": {{(legacyDisabled ? "true" : "false")}}, "updateChannel": "production" }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal(expectedMode, s.Update.UpdateMode);
            Assert.Null(s.Update.LegacyAutoUpdateDisabled);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V6_AbsentAutoUpdateDisabled_DefaultsToAlways()
    {
        var json = """
        {
          "schemaVersion": 6,
          "update": { "updateChannel": "beta" }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("always", s.Update.UpdateMode);
            Assert.Equal("beta", s.Update.UpdateChannel);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_TryxOverlay_PredatingPositionFontAndSize_DefaultsThem()
    {
        // A settings.json from before the position/font/size overlay fields shipped:
        // only the original stats/color/align/filter/opacity keys are present.
        var json = """
        {
          "schemaVersion": 7,
          "tryx": {
            "overlayStats": ["CPU Temperature"],
            "overlayColor": "#ff0000",
            "overlayAlign": "Right",
            "overlayOpacity": 75
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(new[] { "CPU Temperature" }, s.Tryx.OverlayStats);
            Assert.Equal("#ff0000", s.Tryx.OverlayColor);
            Assert.Empty(s.Tryx.OverlayPosX);
            Assert.Empty(s.Tryx.OverlayPosY);
            Assert.Equal("roboto-regular", s.Tryx.OverlayFont);
            Assert.Equal(100, s.Tryx.OverlaySize);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_TryxOverlay_PositionFontAndSize_RoundTripThroughRealJson()
    {
        var json = """
        {
          "schemaVersion": 7,
          "tryx": {
            "overlayStats": ["CPU Temperature", "GPU Temperature"],
            "overlayPosX": [0.03, 0.5],
            "overlayPosY": [0.1, 0.6],
            "overlayFont": "roboto-bold",
            "overlaySize": 120
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(new[] { 0.03, 0.5 }, s.Tryx.OverlayPosX);
            Assert.Equal(new[] { 0.1, 0.6 }, s.Tryx.OverlayPosY);
            Assert.Equal("roboto-bold", s.Tryx.OverlayFont);
            Assert.Equal(120, s.Tryx.OverlaySize);
        }
        finally
        {
            store.Dispose();
        }
    }
}
