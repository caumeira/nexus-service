using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Qos.Service.Models.Widgets;
using Qos.Service.Persistence;
using Qos.Service.Widgets;

namespace Qos.Service.Tests.Widgets;

public class WidgetSettingsServiceTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly JsonConfigStore _store;

    public WidgetSettingsServiceTests()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), "qos-widget-settings-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        _store = new JsonConfigStore(_settingsPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_settingsPath); } catch { /* best effort */ }
    }

    private static WidgetManifest ManifestWithSettings(params (string Key, string DefaultJson)[] entries)
    {
        var m = new WidgetManifest { Id = "com.nexusqos.test", Name = "Test", Version = "1.0.0", Schema = "qos.widget/2" };
        foreach (var (key, defaultJson) in entries)
        {
            using var doc = JsonDocument.Parse(defaultJson);
            m.Settings.Add(new WidgetManifestSettingEntry { Key = key, Type = "string", Default = doc.RootElement.Clone() });
        }
        return m;
    }

    [Fact]
    public void Get_returns_manifest_defaults_when_no_overrides()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""), ("scale", "1"));
        var doc = svc.Get("com.nexusqos.test", manifest);

        Assert.Equal(2, doc.Values.Count);
        Assert.Equal("#ff8800", doc.Values["color"].GetString());
        Assert.Equal(1, doc.Values["scale"].GetInt32());
    }

    [Fact]
    public void Apply_persists_overrides_and_round_trips_through_reload()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""), ("scale", "1"));

        using var colorDoc = JsonDocument.Parse("\"#00ffaa\"");
        var patch = new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = colorDoc.RootElement.Clone() },
        };
        svc.Apply("com.nexusqos.test", manifest, patch);
        _store.FlushNow();

        // Reload via a fresh store pointing at the same file (simulates a
        // qos-service restart).
        using var reloaded = new JsonConfigStore(_settingsPath);
        var reloadedSvc = new WidgetSettingsService(reloaded);
        var got = reloadedSvc.Get("com.nexusqos.test", manifest);
        Assert.Equal("#00ffaa", got.Values["color"].GetString());
        // Unchanged key still shows the manifest default.
        Assert.Equal(1, got.Values["scale"].GetInt32());
    }

    [Fact]
    public void Apply_reset_drops_back_to_manifest_default()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""));

        using var override1 = JsonDocument.Parse("\"#123456\"");
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = override1.RootElement.Clone() },
        });
        var doc1 = svc.Get("com.nexusqos.test", manifest);
        Assert.Equal("#123456", doc1.Values["color"].GetString());

        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch { Reset = new List<string> { "color" } });
        var doc2 = svc.Get("com.nexusqos.test", manifest);
        Assert.Equal("#ff8800", doc2.Values["color"].GetString());
    }

    [Fact]
    public void Apply_set_then_reset_overlap_reset_wins()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""));

        using var doc = JsonDocument.Parse("\"#ABCDEF\"");
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = doc.RootElement.Clone() },
            Reset = new List<string> { "color" },
        });

        var got = svc.Get("com.nexusqos.test", manifest);
        Assert.Equal("#ff8800", got.Values["color"].GetString()); // reset wins
    }

    [Fact]
    public void Apply_empty_patch_does_not_insert_a_hollow_widget_entry()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""));
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch());
        _store.FlushNow();

        var loaded = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
        var widgets = loaded.GetProperty("widgets");
        Assert.False(widgets.TryGetProperty("com.nexusqos.test", out _));
    }

    [Fact]
    public void Reset_only_removes_the_widget_entry_when_no_overrides_remain()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""));

        using var doc = JsonDocument.Parse("\"#abcdef\"");
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["color"] = doc.RootElement.Clone() },
        });
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch { Reset = new List<string> { "color" } });
        _store.FlushNow();

        var loaded = JsonDocument.Parse(File.ReadAllText(_settingsPath)).RootElement;
        var widgets = loaded.GetProperty("widgets");
        Assert.False(widgets.TryGetProperty("com.nexusqos.test", out _));
    }

    [Fact]
    public void Settings_round_trip_handles_quoted_strings_unicode_and_large_blobs()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("blob", "\"\""));
        var weird = "He said \"hi\" — 🎉 " + new string('x', 4096);
        var stored = JsonSerializer.Serialize(weird);
        using var doc = JsonDocument.Parse(stored);
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement> { ["blob"] = doc.RootElement.Clone() },
        });
        _store.FlushNow();
        using var reloaded = new JsonConfigStore(_settingsPath);
        var got = new WidgetSettingsService(reloaded).Get("com.nexusqos.test", manifest);
        Assert.Equal(weird, got.Values["blob"].GetString());
    }

    [Fact]
    public void Apply_silently_drops_keys_not_in_manifest()
    {
        var svc = new WidgetSettingsService(_store);
        var manifest = ManifestWithSettings(("color", "\"#ff8800\""));

        using var stray = JsonDocument.Parse("\"hostile\"");
        svc.Apply("com.nexusqos.test", manifest, new WidgetSettingsPatch
        {
            Set = new Dictionary<string, JsonElement>
            {
                ["color"] = JsonDocument.Parse("\"#abcdef\"").RootElement.Clone(),
                ["unknown.key"] = stray.RootElement.Clone(),
            },
        });
        var doc = svc.Get("com.nexusqos.test", manifest);
        Assert.False(doc.Values.ContainsKey("unknown.key"));
        Assert.Equal("#abcdef", doc.Values["color"].GetString());
    }
}
