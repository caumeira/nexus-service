using System.Text.Json;
using Nexus.Service.Lighting.GameSync;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public class Cs2GsiPayloadDeserializationTests
{
    private const string SampleJson = """
        {
            "provider": {
                "name": "Counter-Strike 2",
                "appid": 730,
                "steamid": "76561198000000000",
                "timestamp": 1718600000
            },
            "player": {
                "team": "CT",
                "activity": "playing",
                "state": {
                    "health": 85,
                    "armor": 100,
                    "helmet": true,
                    "defusekit": false,
                    "flashed": 0,
                    "smoked": 0,
                    "burning": 0,
                    "money": 3400,
                    "round_kills": 1
                }
            },
            "round": {
                "phase": "live",
                "bomb": ""
            },
            "bomb": {
                "state": "",
                "countdown": ""
            },
            "map": {
                "name": "de_dust2",
                "phase": "live",
                "team_ct": { "score": 5 },
                "team_t": { "score": 4 }
            },
            "phase_countdowns": {
                "phase": "live",
                "phase_ends_in": "95.0"
            },
            "auth": {
                "token": "nexus_test_token"
            }
        }
        """;

    [Fact]
    public void Deserialize_ThenMapToColor_CtFullHealthYieldsCtBase()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload);
        var (r, g, b) = GsiLightingMapper.MapToColor(payload!);

        // CT, health=85, factor=0.85: r=255+(0-255)*0.85=255-216=39, g=(120)*0.85=102, b=(255)*0.85=216.
        Assert.Equal((byte)39, r);
        Assert.Equal((byte)102, g);
        Assert.Equal((byte)216, b);
    }
}
