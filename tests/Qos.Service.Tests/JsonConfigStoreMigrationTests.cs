using Qos.Service.Persistence;

namespace Qos.Service.Tests;

/// <summary>
/// Verifies JsonConfigStore is a no-op on records already at the current
/// schema version. The legacy V1-V4 migration tests were removed alongside
/// the migration code itself (qos-service drop-legacy-schema-migrations
/// refactor).
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
            Assert.Equal(QosSettings.CurrentSchemaVersion, s.SchemaVersion);
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
}
