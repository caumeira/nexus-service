using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// One-time v5 to v6 settings migration: per-card LED map overrides and
/// aspect ratios re-key into the device-scoped segment-local dicts via each
/// provider's default-partition card-id mapping; unknown keys become
/// single-segment devices.
/// </summary>
public class LegacyLedOverrideMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public LegacyLedOverrideMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-zonemig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── card-id mapping table ────────────────────────────────────────────

    [Theory]
    [InlineData("keeb:SER1:keys", "keeb:SER1", 0)]
    [InlineData("keeb:SER1:underglow", "keeb:SER1", 1)]
    [InlineData("openrgb-0-1", "openrgb-0", 1)]
    [InlineData("openrgb-s-MB01-2", "openrgb-s-MB01", 2)]
    [InlineData("openrgb-l-COM4-0", "openrgb-l-COM4", 0)]
    public void Known_multi_segment_shapes_map_to_their_segment(string legacy, string deviceId, int segment)
    {
        Assert.Equal((deviceId, segment), LegacyLedOverrideMigration.MapLegacyCardId(legacy));
    }

    [Theory]
    [InlineData("openrgb-5")]               // legacy numeric whole-device id
    [InlineData("openrgb-s-123")]           // whole device with a numeric serial
    [InlineData("openrgb-s-ABC")]           // whole device, serial without dashes
    [InlineData("openrgb-s-X-2024")]        // numeric tail too large for a zone index
    [InlineData("np50:hub1:port2")]         // hub port card
    [InlineData("hue:bridge:light-3000")]   // smart light
    [InlineData("keeb:SER1")]               // bare keeb hub id
    public void Everything_else_maps_as_single_segment_device(string legacy)
    {
        Assert.Equal((legacy, 0), LegacyLedOverrideMigration.MapLegacyCardId(legacy));
    }

    // ── in-place migration ───────────────────────────────────────────────

    [Fact]
    public void Apply_moves_overrides_into_segment_space_and_clears_legacy()
    {
        var settings = new NexusSettings();
        settings.Devices.LedMapOverrides["keeb:SER1:keys"] = new()
        {
            new LedPositionOverride { LedIndex = 3, U = 0.1f, V = 0.2f },
        };
        settings.Devices.LedMapOverrides["keeb:SER1:underglow"] = new()
        {
            new LedPositionOverride { LedIndex = 7, U = 0.3f, V = 0.4f, Disabled = true },
        };
        settings.Devices.LedMapOverrides["openrgb-s-MB01-1"] = new()
        {
            new LedPositionOverride { LedIndex = 12, U = 0.5f, V = 0.6f },
        };
        settings.Devices.LedMapOverrides["np50:hub1:port2"] = new()
        {
            new LedPositionOverride { LedIndex = 0, U = 0.7f, V = 0.8f },
        };
        settings.Devices.LedMapAspectRatios["keeb:SER1:keys"] = 4.2f;
        settings.Devices.LedMapAspectRatios["keeb:SER1:underglow"] = 9.9f;

        LegacyLedOverrideMigration.Apply(settings);

        // Both keeb cards merge into the hub device, segment-local.
        var keeb = settings.Devices.DeviceLedOverrides["keeb:SER1"];
        Assert.Equal(2, keeb.Count);
        Assert.Contains(keeb, o => o is { Segment: 0, LedIndex: 3, U: 0.1f });
        Assert.Contains(keeb, o => o is { Segment: 1, LedIndex: 7, Disabled: true });

        var mobo = settings.Devices.DeviceLedOverrides["openrgb-s-MB01"];
        Assert.Single(mobo);
        Assert.Equal(1, mobo[0].Segment);
        Assert.Equal(12, mobo[0].LedIndex);

        var port = settings.Devices.DeviceLedOverrides["np50:hub1:port2"];
        Assert.Single(port);
        Assert.Equal(0, port[0].Segment);

        // First mapped card wins on the device aspect ratio.
        Assert.Equal(4.2f, settings.Devices.DeviceAspectRatios["keeb:SER1"]);

        Assert.Empty(settings.Devices.LedMapOverrides);
        Assert.Empty(settings.Devices.LedMapAspectRatios);
    }

    // ── load-time migration through JsonConfigStore ──────────────────────

    [Fact]
    public void Load_migrates_v5_settings_and_persists_v6()
    {
        var v5Json = """
        {
          "schemaVersion": 5,
          "devices": {
            "ledMapOverrides": {
              "keeb:SER1:keys": [ { "ledIndex": 2, "u": 0.25, "v": 0.75, "disabled": false } ],
              "openrgb-0-1": [ { "ledIndex": 5, "u": 0.5, "v": 0.5, "disabled": true } ],
              "openrgb-s-XYZ": [ { "ledIndex": 1, "u": 0.9, "v": 0.1 } ]
            },
            "ledMapAspectRatios": { "openrgb-s-XYZ": 2.5 }
          }
        }
        """;
        File.WriteAllText(_settingsPath, v5Json);

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Empty(s.Devices.LedMapOverrides);
            Assert.Empty(s.Devices.LedMapAspectRatios);

            var keeb = s.Devices.DeviceLedOverrides["keeb:SER1"];
            Assert.Single(keeb);
            Assert.Equal((0, 2), (keeb[0].Segment, keeb[0].LedIndex));

            var mobo = s.Devices.DeviceLedOverrides["openrgb-0"];
            Assert.Single(mobo);
            Assert.Equal((1, 5), (mobo[0].Segment, mobo[0].LedIndex));
            Assert.True(mobo[0].Disabled);

            var single = s.Devices.DeviceLedOverrides["openrgb-s-XYZ"];
            Assert.Equal((0, 1), (single[0].Segment, single[0].LedIndex));
            Assert.Equal(2.5f, s.Devices.DeviceAspectRatios["openrgb-s-XYZ"]);
        }
        finally
        {
            store.Dispose();
        }

        // The migrated document was persisted: a fresh store sees v6 data
        // without re-running the migration.
        var second = new JsonConfigStore(_settingsPath);
        try
        {
            var s2 = second.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s2.SchemaVersion);
            Assert.Single(s2.Devices.DeviceLedOverrides["keeb:SER1"]);
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void Load_noops_on_current_schema()
    {
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "devices": { "deviceAspectRatios": { "dev-1": 1.5 } }
        }
        """);
        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.Equal(1.5f, s.Devices.DeviceAspectRatios["dev-1"]);
            Assert.Empty(s.Devices.DeviceLedOverrides);
        }
        finally
        {
            store.Dispose();
        }
    }
}
