using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class LayoutPresetSerializationTests
{
    private static readonly JsonSerializerOptions PersistenceOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void LayoutPresets_round_trip_through_source_gen_context()
    {
        var settings = new NexusSettings
        {
            Lighting = new LightingSettings
            {
                ActiveLayoutPresetId = "preset-a",
                LayoutPresets =
                {
                    new LayoutPreset
                    {
                        Id = "preset-a",
                        Name = "Gaming",
                        Layouts = new Dictionary<string, DeviceLayout>
                        {
                            ["openrgb-0"] = new() { X = 10, Y = 20, W = 80, H = 40, Rotation = 90 },
                        },
                        DisabledDevices = new List<string> { "dev-off-1", "dev-off-2" },
                        UncontrolledDevices = new List<string> { "dev-ignored-1" },
                    },
                    new LayoutPreset
                    {
                        Id = "preset-b",
                        Name = "Work",
                        Layouts = new Dictionary<string, DeviceLayout>
                        {
                            ["openrgb-1"] = new() { X = 5, Y = 5, W = 60, H = 30, Rotation = 0 },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(loaded);
        Assert.Equal("preset-a", loaded.Lighting.ActiveLayoutPresetId);
        Assert.Equal(2, loaded.Lighting.LayoutPresets.Count);

        var a = loaded.Lighting.LayoutPresets[0];
        Assert.Equal("preset-a", a.Id);
        Assert.Equal("Gaming", a.Name);
        Assert.True(a.Layouts.TryGetValue("openrgb-0", out var layout0));
        Assert.Equal(10f, layout0.X);
        Assert.Equal(20f, layout0.Y);
        Assert.Equal(80f, layout0.W);
        Assert.Equal(40f, layout0.H);
        Assert.Equal(90, layout0.Rotation);
        Assert.Equal(new List<string> { "dev-off-1", "dev-off-2" }, a.DisabledDevices);
        Assert.Equal(new List<string> { "dev-ignored-1" }, a.UncontrolledDevices);

        var b = loaded.Lighting.LayoutPresets[1];
        Assert.Equal("preset-b", b.Id);
        Assert.Equal("Work", b.Name);
        Assert.Null(b.DisabledDevices);
        Assert.Null(b.UncontrolledDevices);
    }

    [Fact]
    public void Preset_look_round_trips_through_source_gen_context()
    {
        var settings = new NexusSettings
        {
            Lighting = new LightingSettings
            {
                LayoutPresets =
                {
                    new LayoutPreset
                    {
                        Id = "preset-a",
                        Name = "Gaming",
                        Look = new LightingPresetLook
                        {
                            Sync = "jellyfish",
                            AnimateEffect = "jellyfish",
                            StaticEffect = "simplered",
                            AnimateSlot = 2,
                            StaticSlot = 1,
                            GlobalBrightness = 0.35f,
                            LastMediaId = "clip-7",
                            AnimateState = new AnimateEffectState
                            {
                                Speed = 40,
                                Intensity = 0.8f,
                                Hue = 0.25f,
                                Params = new Dictionary<string, float> { ["u_zoom"] = 1.5f },
                            },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        var look = Assert.Single(loaded!.Lighting.LayoutPresets).Look;
        Assert.NotNull(look);
        Assert.Equal("jellyfish", look.Sync);
        Assert.Equal("jellyfish", look.AnimateEffect);
        Assert.Equal("simplered", look.StaticEffect);
        Assert.Equal(2, look.AnimateSlot);
        Assert.Equal(1, look.StaticSlot);
        Assert.Equal(0.35f, look.GlobalBrightness);
        Assert.Equal("clip-7", look.LastMediaId);
        Assert.NotNull(look.AnimateState);
        Assert.Equal(40, look.AnimateState.Speed);
        Assert.Equal(0.25f, look.AnimateState.Hue);
        Assert.Equal(1.5f, look.AnimateState.Params["u_zoom"]);
        Assert.Null(look.StaticState);
    }

    [Fact]
    public void Look_defaults_to_null_when_field_absent_in_json()
    {
        const string json = """
        {
          "schemaVersion": 7,
          "lighting": {
            "layoutPresets": [
              { "id": "p1", "name": "Old Preset", "layouts": {} }
            ]
          }
        }
        """;

        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.Null(Assert.Single(loaded!.Lighting.LayoutPresets).Look);
    }

    [Fact]
    public void DisabledDevices_defaults_to_null_when_field_absent_in_json()
    {
        const string json = """
        {
          "schemaVersion": 7,
          "lighting": {
            "layoutPresets": [
              {
                "id": "p1",
                "name": "Old Preset",
                "layouts": {}
              }
            ]
          }
        }
        """;

        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Lighting.LayoutPresets);
        Assert.Null(loaded.Lighting.LayoutPresets[0].DisabledDevices);
    }

    [Fact]
    public void Old_settings_json_missing_preset_fields_deserializes_to_safe_defaults()
    {
        // A settings.json written before LayoutPresets was added has no
        // layoutPresets or activeLayoutPresetId fields. They must default to
        // empty list and null without failing deserialization.
        const string oldJson = """
        {
          "schemaVersion": 7,
          "lighting": {
            "sync": "animate",
            "frameRate": 30,
            "scaleRatio": 1.0,
            "deviceLayouts": {
              "openrgb-0": { "x": 5, "y": 10, "w": 80, "h": 40, "rotation": 0 }
            }
          }
        }
        """;

        var loaded = JsonSerializer.Deserialize(oldJson, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Lighting.LayoutPresets);
        Assert.Null(loaded.Lighting.ActiveLayoutPresetId);
        Assert.True(loaded.Lighting.DeviceLayouts.ContainsKey("openrgb-0"));
    }
}
