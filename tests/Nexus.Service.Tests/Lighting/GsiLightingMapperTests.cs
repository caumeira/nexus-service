using Nexus.Service.Lighting.GameSync;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Tests.Lighting;

public class GsiLightingMapperTests
{
    private static Cs2GsiPayload LivePayload(string team, int health, string? roundPhase = null, string? activity = "playing")
    {
        return new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = team,
                Activity = activity,
                State = new Cs2GsiPlayerState { Health = health },
            },
            Round = roundPhase is null ? null : new Cs2GsiRound { Phase = roundPhase },
        };
    }

    [Fact]
    public void MapToColor_NullPlayer_ReturnsIdle()
    {
        var p = new Cs2GsiPayload();
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)8, r);
        Assert.Equal((byte)8, g);
        Assert.Equal((byte)8, b);
    }

    [Fact]
    public void MapToColor_NullState_ReturnsIdle()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer { Team = "CT", Activity = "playing" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)8, r);
        Assert.Equal((byte)8, g);
        Assert.Equal((byte)8, b);
    }

    [Fact]
    public void MapToColor_ActivityMenu_ReturnsIdle()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "menu",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)8, r);
        Assert.Equal((byte)8, g);
        Assert.Equal((byte)8, b);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(128)]
    [InlineData(255)]
    public void MapToColor_Flashed_ReturnsGrayScaledByFlash(int flashed)
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100, Flashed = flashed },
            },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)flashed, r);
        Assert.Equal((byte)flashed, g);
        Assert.Equal((byte)flashed, b);
    }

    [Fact]
    public void MapToColor_FlashedBelowThreshold_DoesNotFlash()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100, Flashed = 30 },
            },
        };
        // Flashed=30 is at threshold, not above; should not be the flash path.
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        // Health=100 CT full health: (0,120,255).
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)120, g);
        Assert.Equal((byte)255, b);
    }

    [Fact]
    public void MapToColor_Burning_ReturnsOrange()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "T",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100, Burning = 50 },
            },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)90, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_BombDefused_ReturnsGreen()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 80 },
            },
            Bomb = new Cs2GsiBomb { State = "defused" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)255, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_BombExploded_ReturnsRed()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "T",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 80 },
            },
            Bomb = new Cs2GsiBomb { State = "exploded" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_BombPlanted_NoCountdown_BrightnessAtLow()
    {
        // No countdown string -> default 40s -> t=0 -> brightness=120.
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "T",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
            Bomb = new Cs2GsiBomb { State = "planted" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)120, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_BombPlanted_ZeroCountdown_BrightnessAtHigh()
    {
        // countdown=0 -> t=1 -> brightness=120+135=255.
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "T",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
            Bomb = new Cs2GsiBomb { State = "planted", Countdown = "0.0" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_BombDefusing_ProducesRedRamp()
    {
        // State="defusing" with 20s remaining: t=0.5 -> brightness=120+67=187.
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
            Bomb = new Cs2GsiBomb { State = "defusing", Countdown = "20.0" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)187, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_RoundBombPlanted_ProducesRedRamp()
    {
        // round.Bomb="planted" with no Bomb object: default 40s -> brightness=120.
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
            Round = new Cs2GsiRound { Bomb = "planted" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)120, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_RoundOver_Winner_ReturnsGreen()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 50 },
            },
            Round = new Cs2GsiRound { Phase = "over", WinTeam = "CT" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)200, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_RoundOver_Loser_ReturnsRed()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 50 },
            },
            Round = new Cs2GsiRound { Phase = "over", WinTeam = "T" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)200, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_Freezetime_ReturnsFreezeBlue()
    {
        var p = new Cs2GsiPayload
        {
            Player = new Cs2GsiPlayer
            {
                Team = "CT",
                Activity = "playing",
                State = new Cs2GsiPlayerState { Health = 100 },
            },
            Round = new Cs2GsiRound { Phase = "freezetime" },
        };
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)80, g);
        Assert.Equal((byte)255, b);
    }

    [Fact]
    public void MapToColor_LiveCT_FullHealth_ReturnsCtBase()
    {
        var p = LivePayload("CT", 100);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)120, g);
        Assert.Equal((byte)255, b);
    }

    [Fact]
    public void MapToColor_LiveT_FullHealth_ReturnsTBase()
    {
        var p = LivePayload("T", 100);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)180, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_LiveCT_ZeroHealth_ReturnsRed()
    {
        var p = LivePayload("CT", 0);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_LiveT_ZeroHealth_ReturnsRed()
    {
        var p = LivePayload("T", 0);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)255, r);
        Assert.Equal((byte)0, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_UnknownTeam_FullHealth_ReturnsUnknownBase()
    {
        var p = LivePayload("Spectator", 100);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)0, r);
        Assert.Equal((byte)200, g);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void MapToColor_LiveCT_HalfHealth_IsInterpolatedBetweenRedAndBase()
    {
        // CT base (0,120,255), health=50, factor=0.5.
        // r = 255 + (0-255)*0.5 = 255-127 = 128 (truncated int cast).
        // g = 0 + (120-0)*0.5 = 60.
        // b = 0 + (255-0)*0.5 = 127.
        var p = LivePayload("CT", 50);
        var (r, g, b) = GsiLightingMapper.MapToColor(p);
        Assert.Equal((byte)128, r);
        Assert.Equal((byte)60, g);
        Assert.Equal((byte)127, b);
    }
}
