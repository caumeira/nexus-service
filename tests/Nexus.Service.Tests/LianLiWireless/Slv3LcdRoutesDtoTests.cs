using System.Text.Json;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Pins the wire shape of the LCD route DTOs. nexus-web builds against these
/// exact field names (see plans/lianli-wireless-support.md section 5); a
/// rename here is a breaking change for the panel.
/// </summary>
public class Slv3LcdRoutesDtoTests
{
    [Fact]
    public void ScreensResponse_serializes_the_documented_field_names()
    {
        var response = new Slv3LcdScreensResponse
        {
            Screens =
            {
                new Slv3LcdScreenDto
                {
                    Serial = "AAAA111122223333",
                    Position = 1,
                    Brightness = 80,
                    Rotation = 2,
                    ContentType = "image",
                    MediaId = "media-1",
                    SensorSource = "cpuTemp",
                    SensorStyle = "ring",
                    ClockFace = "digital",
                    AnimationId = "pulse",
                    ColorA = "#00D1FF",
                    ColorB = "#FFFFFF",
                    TempUnit = "c",
                },
            },
        };

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.Slv3LcdScreensResponse);
        using var doc = JsonDocument.Parse(json);
        var screen = doc.RootElement.GetProperty("screens")[0];

        Assert.Equal("AAAA111122223333", screen.GetProperty("serial").GetString());
        Assert.Equal(1, screen.GetProperty("position").GetInt32());
        Assert.Equal(400, screen.GetProperty("width").GetInt32());
        Assert.Equal(400, screen.GetProperty("height").GetInt32());
        Assert.Equal(80, screen.GetProperty("brightness").GetInt32());
        Assert.Equal(2, screen.GetProperty("rotation").GetInt32());
        Assert.Equal("image", screen.GetProperty("contentType").GetString());
        Assert.Equal("media-1", screen.GetProperty("mediaId").GetString());
        Assert.Equal("cpuTemp", screen.GetProperty("sensorSource").GetString());
        Assert.Equal("ring", screen.GetProperty("sensorStyle").GetString());
        Assert.Equal("digital", screen.GetProperty("clockFace").GetString());
        Assert.Equal("pulse", screen.GetProperty("animationId").GetString());
        Assert.Equal("#00D1FF", screen.GetProperty("colorA").GetString());
        Assert.Equal("#FFFFFF", screen.GetProperty("colorB").GetString());
        Assert.Equal("c", screen.GetProperty("tempUnit").GetString());
    }

    [Fact]
    public void SettingsRequest_deserializes_the_documented_field_names()
    {
        const string json = """{ "serial": "SER1", "brightness": 50, "rotation": 1 }""";

        var body = JsonSerializer.Deserialize(json, AppJsonContext.Default.Slv3LcdSettingsRequest);

        Assert.NotNull(body);
        Assert.Equal("SER1", body.Serial);
        Assert.Equal((byte)50, body.Brightness);
        Assert.Equal((byte)1, body.Rotation);
    }

    [Fact]
    public void ContentRequest_deserializes_the_documented_field_names()
    {
        const string json = """{ "serial": "SER1", "contentType": "video", "mediaId": "media-2" }""";

        var body = JsonSerializer.Deserialize(json, AppJsonContext.Default.Slv3LcdContentRequest);

        Assert.NotNull(body);
        Assert.Equal("SER1", body.Serial);
        Assert.Equal("video", body.ContentType);
        Assert.Equal("media-2", body.MediaId);
    }

    [Fact]
    public void ContentRequest_deserializes_the_sensor_clock_animation_field_names()
    {
        const string json = """
        {
          "serial": "SER1",
          "contentType": "sensor",
          "sensorSource": "fanRpm",
          "sensorStyle": "bar",
          "clockFace": "analogClassic",
          "animationId": "spectrum",
          "colorA": "#112233",
          "colorB": "#445566",
          "tempUnit": "f"
        }
        """;

        var body = JsonSerializer.Deserialize(json, AppJsonContext.Default.Slv3LcdContentRequest);

        Assert.NotNull(body);
        Assert.Equal("sensor", body.ContentType);
        Assert.Equal("fanRpm", body.SensorSource);
        Assert.Equal("bar", body.SensorStyle);
        Assert.Equal("analogClassic", body.ClockFace);
        Assert.Equal("spectrum", body.AnimationId);
        Assert.Equal("#112233", body.ColorA);
        Assert.Equal("#445566", body.ColorB);
        Assert.Equal("f", body.TempUnit);
    }

    [Fact]
    public void MediaListResponse_serializes_id_name_kind()
    {
        var response = new Slv3LcdMediaListResponse
        {
            Items = { new Slv3LcdMediaDto { Id = "abc123", Name = "clip.mp4", Kind = "video" } },
        };

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.Slv3LcdMediaListResponse);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];

        Assert.Equal("abc123", item.GetProperty("id").GetString());
        Assert.Equal("clip.mp4", item.GetProperty("name").GetString());
        Assert.Equal("video", item.GetProperty("kind").GetString());
        // The persisted-only fields (frames, delays, importedAtUnixMs) must not
        // leak into the wire DTO; the response type carries id/name/kind only.
        Assert.False(item.TryGetProperty("frames", out _));
    }

    [Fact]
    public void ImportResponse_serializes_mediaId_name_kind()
    {
        var response = new Slv3LcdImportResponse { MediaId = "abc123", Name = "clip.mp4", Kind = "video" };

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.Slv3LcdImportResponse);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("abc123", doc.RootElement.GetProperty("mediaId").GetString());
        Assert.Equal("clip.mp4", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("video", doc.RootElement.GetProperty("kind").GetString());
    }
}
