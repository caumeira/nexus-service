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
    public void Deserialize_SamplePayload_ProviderFields()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload);
        Assert.NotNull(payload.Provider);
        Assert.Equal("Counter-Strike 2", payload.Provider!.Name);
        Assert.Equal(730, payload.Provider.AppId);
        Assert.Equal("76561198000000000", payload.Provider.SteamId);
        Assert.Equal(1718600000L, payload.Provider.Timestamp);
    }

    [Fact]
    public void Deserialize_SamplePayload_PlayerFields()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload?.Player);
        Assert.Equal("CT", payload!.Player!.Team);
        Assert.Equal("playing", payload.Player.Activity);
        Assert.NotNull(payload.Player.State);
        Assert.Equal(85, payload.Player.State!.Health);
        Assert.Equal(100, payload.Player.State.Armor);
        Assert.True(payload.Player.State.Helmet);
        Assert.False(payload.Player.State.Defusekit);
        Assert.Equal(3400, payload.Player.State.Money);
        Assert.Equal(1, payload.Player.State.RoundKills);
    }

    [Fact]
    public void Deserialize_SamplePayload_MapFields()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload?.Map);
        Assert.Equal("de_dust2", payload!.Map!.Name);
        Assert.Equal(5, payload.Map.TeamCt?.Score);
        Assert.Equal(4, payload.Map.TeamT?.Score);
    }

    [Fact]
    public void Deserialize_SamplePayload_AuthToken()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload?.Auth);
        Assert.Equal("nexus_test_token", payload!.Auth!.Token);
    }

    [Fact]
    public void Deserialize_SamplePayload_PhaseCountdowns()
    {
        var payload = JsonSerializer.Deserialize(SampleJson, AppJsonContext.Default.Cs2GsiPayload);

        Assert.NotNull(payload?.PhaseCountdowns);
        Assert.Equal("live", payload!.PhaseCountdowns!.Phase);
        Assert.Equal("95.0", payload.PhaseCountdowns.PhaseEndsIn);
    }

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
