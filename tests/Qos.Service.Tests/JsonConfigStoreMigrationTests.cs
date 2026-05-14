using System.Text.Json;
using System.Text.Json.Nodes;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

/// <summary>
/// Verifies the v1 → v2 schema migration in <see cref="JsonConfigStore"/>:
/// Ui.{theme/panel/overlay/monitoring} fields move into matching top-level
/// POCOs; Ui.FanChannelOrder moves into Cooling.FanChannelOrder; schemaVersion
/// bumps to 2; the bumped record persists back to disk on next save.
/// </summary>
public class JsonConfigStoreMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public JsonConfigStoreMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-test-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MigratesV1FlatUiFieldsIntoNestedBlocks()
    {
        // A representative v1 settings.json: flat Ui fields, no top-level
        // theme/panel/overlay/monitoring blocks, schemaVersion=1.
        var v1Json = """
        {
          "schemaVersion": 1,
          "ui": {
            "language": "es",
            "themeMode": "dark",
            "accentColor": "#ff00ff",
            "disableConflictAlerts": true,
            "monitoringShowAverage": false,
            "showMacStatusBarIcon": false,
            "showWindowsTrayIcon": false,
            "monitoringDetailedCollapsed": ["cpu", "gpu"],
            "fanChannelOrder": ["fan1", "fan2"],
            "panelAutoLaunch": true,
            "panelThemeSyncWithDesktop": false,
            "panelThemeMode": "light",
            "panelAccentSyncWithDesktop": false,
            "panelAccentColor": "#abcdef",
            "panelBackgroundColor": "#111111",
            "panelBackgroundColorLight": "#eeeeee",
            "panelBackgroundMode": "shader",
            "panelBackgroundEffect": "aurora",
            "panelBackgroundTemplate": 2,
            "panelBackgroundOpacity": 0.7,
            "panelWidgetOpacity": 0.5,
            "panelWidgetLabels": false,
            "dashboardLayout": {
              "layoutSchemaVersion": 2,
              "surface": "desktop",
              "pages": [
                { "id": "p1", "widgets": [
                  { "id": "w1", "type": "lighting", "size": "4x2", "col": 0, "row": 0 }
                ] }
              ]
            },
            "overlayWidgetsEnabled": true,
            "overlayWidgetsAlwaysOnTop": true,
            "overlayWidgetScale": 150,
            "overlayWidgetOpacity": 0.8,
            "overlayWidgetsMonitor": 1,
            "overlayLayout": [
              { "id": "ov1", "type": "clock", "size": "2x2", "monitor": 0, "col": 1, "row": 2 }
            ]
          }
        }
        """;
        File.WriteAllText(_settingsPath, v1Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            // SchemaVersion bumped.
            Assert.Equal(2, s.SchemaVersion);

            // Theme moved.
            Assert.Equal("es", s.Theme.Language);
            Assert.Equal("dark", s.Theme.ThemeMode);
            Assert.Equal("#ff00ff", s.Theme.AccentColor);

            // Monitoring moved.
            Assert.False(s.Monitoring.ShowAverage);
            Assert.False(s.Monitoring.ShowMacStatusBarIcon);
            Assert.False(s.Monitoring.ShowWindowsTrayIcon);
            Assert.Equal(new[] { "cpu", "gpu" }, s.Monitoring.DetailedCollapsed);

            // Panel moved.
            Assert.True(s.Panel.AutoLaunch);
            Assert.False(s.Panel.ThemeSyncWithDesktop);
            Assert.Equal("light", s.Panel.ThemeMode);
            Assert.False(s.Panel.AccentSyncWithDesktop);
            Assert.Equal("#abcdef", s.Panel.AccentColor);
            Assert.Equal("#111111", s.Panel.BackgroundColor);
            Assert.Equal("#eeeeee", s.Panel.BackgroundColorLight);
            Assert.Equal("shader", s.Panel.BackgroundMode);
            Assert.Equal("aurora", s.Panel.BackgroundEffect);
            Assert.Equal(2, s.Panel.BackgroundTemplate);
            Assert.Equal(0.7, s.Panel.BackgroundOpacity);
            Assert.Equal(0.5, s.Panel.WidgetOpacity);
            Assert.False(s.Panel.WidgetLabels);
            Assert.NotNull(s.Panel.DashboardLayout);
            Assert.Equal("desktop", s.Panel.DashboardLayout!.Surface);
            Assert.Single(s.Panel.DashboardLayout.Pages);
            Assert.Equal("lighting", s.Panel.DashboardLayout.Pages[0].Widgets[0].Type);

            // Overlay moved.
            Assert.True(s.Overlay.Enabled);
            Assert.True(s.Overlay.AlwaysOnTop);
            Assert.Equal(150, s.Overlay.Scale);
            Assert.Equal(0.8, s.Overlay.Opacity);
            Assert.Equal(1, s.Overlay.Monitor);
            Assert.Single(s.Overlay.Layout);
            Assert.Equal("clock", s.Overlay.Layout[0].Type);

            // Cooling gained FanChannelOrder.
            Assert.NotNull(s.Cooling.FanChannelOrder);
            Assert.Equal(new[] { "fan1", "fan2" }, s.Cooling.FanChannelOrder);

            // Ui retains only the residual field.
            Assert.True(s.Ui.DisableConflictAlerts);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoOpOnAlreadyV2()
    {
        // A v2 settings.json: nested blocks already in place, schemaVersion=2.
        var v2Json = """
        {
          "schemaVersion": 2,
          "theme": { "themeMode": "dark", "accentColor": "#aabbcc", "language": "en" },
          "panel": { "autoLaunch": true, "themeMode": "system" },
          "overlay": { "enabled": true, "scale": 120 },
          "monitoring": { "showAverage": true }
        }
        """;
        File.WriteAllText(_settingsPath, v2Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(2, s.SchemaVersion);
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

    [Fact]
    public void Load_MissingSchemaVersionMigratesAsV1()
    {
        // Older builds didn't carry SchemaVersion at all. Migration must still
        // run — the absent property reads as 0 < 2 so the v1→v2 path triggers.
        var legacyJson = """
        {
          "ui": {
            "themeMode": "light",
            "panelAutoLaunch": true
          }
        }
        """;
        File.WriteAllText(_settingsPath, legacyJson);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(2, s.SchemaVersion);
            Assert.Equal("light", s.Theme.ThemeMode);
            Assert.True(s.Panel.AutoLaunch);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_MigratedRecordPersistsOnNextSave()
    {
        var v1Json = """
        {
          "schemaVersion": 1,
          "ui": { "panelAutoLaunch": true, "themeMode": "dark" }
        }
        """;
        File.WriteAllText(_settingsPath, v1Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            store.FlushNow(); // force the debounced write to land
        }
        finally
        {
            store.Dispose();
        }

        // Re-read the file from disk and check it now carries the nested
        // shape — the migration is persisted, not a load-time-only fixup.
        var onDisk = File.ReadAllText(_settingsPath);
        var doc = JsonNode.Parse(onDisk) as JsonObject;
        Assert.NotNull(doc);
        Assert.Equal(2, doc!["schemaVersion"]!.GetValue<int>());
        Assert.Equal("dark", doc["theme"]!["themeMode"]!.GetValue<string>());
        Assert.True(doc["panel"]!["autoLaunch"]!.GetValue<bool>());
        // ui block should no longer carry the moved field.
        var ui = doc["ui"] as JsonObject;
        Assert.True(ui is null || !ui.ContainsKey("panelAutoLaunch"));
    }
}
