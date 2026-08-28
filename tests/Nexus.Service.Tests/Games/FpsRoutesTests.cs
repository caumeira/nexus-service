using Nexus.Service.Games;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Games;

public class FpsRoutesTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(6000, 100, 60)]
    [InlineData(590 * 144, 590, 144)]
    public void AverageFps_IsFramesOverValidSec(long frames, long validSec, double expected)
    {
        Assert.Equal(expected, FpsRoutes.AverageFps(frames, validSec));
    }

    [Fact]
    public void AverageFps_ZeroValidSec_ReturnsZero_NotDivideByZero()
    {
        Assert.Equal(0, FpsRoutes.AverageFps(1000, 0));
    }

    [Fact]
    public void ToGameDto_SteamGame_ParsesSteamAppIdAsANumber()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        FpsHistogram.AddSample(hist, 60);
        var summary = new FpsGameSummary(
            "steam:1091500", "Cyberpunk 2077", "steam", "1091500",
            3, 1800, 1750, 1750 * 60, 30, 144, hist, 1_700_000_000_000);

        var dto = FpsRoutes.ToGameDto(summary);

        Assert.Equal("steam:1091500", dto.GameKey);
        Assert.Equal("Cyberpunk 2077", dto.Name);
        Assert.Equal("steam", dto.Store);
        Assert.Equal(1091500, dto.SteamAppId);
        Assert.Equal(3, dto.Sessions);
        Assert.Equal(1800, dto.FocusedSec);
        Assert.Equal(60, dto.AvgFps);
        Assert.Equal(30, dto.MinFps);
        Assert.Equal(144, dto.MaxFps);
        Assert.Equal(1_700_000_000_000, dto.LastPlayedUtcMs);
    }

    [Fact]
    public void ToGameDto_NonSteamGame_HasNullSteamAppId()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        var summary = new FpsGameSummary("epic:fortnite", "Fortnite", "epic", null, 1, 600, 590, 590 * 60, 40, 100, hist, 0);

        var dto = FpsRoutes.ToGameDto(summary);

        Assert.Null(dto.SteamAppId);
    }

    [Fact]
    public void ToSessionDto_MapsEveryField()
    {
        var hist = new uint[FpsHistogram.BucketCount];
        FpsHistogram.AddSample(hist, 90);
        var record = new FpsSessionRecord(
            Guid.NewGuid(), "steam:1", "Game", "steam", 1_000, 2_000, 600, 590, 590 * 90,
            30, 144, hist, 2560, 1440, 144, 2560, 1440, true, true, 90, 12345, FpsUploadState.Pending);

        var dto = FpsRoutes.ToSessionDto(record);

        Assert.Equal(record.Id.ToString(), dto.Id);
        Assert.Equal(1_000, dto.StartedUtcMs);
        Assert.Equal(2_000, dto.EndedUtcMs);
        Assert.Equal(600, dto.FocusedSec);
        Assert.Equal(590, dto.ValidSec);
        Assert.Equal(90, dto.AvgFps);
        Assert.Equal(30, dto.MinFps);
        Assert.Equal(144, dto.MaxFps);
        Assert.Equal(2560, dto.DispW);
        Assert.Equal(1440, dto.DispH);
        Assert.Equal(144, dto.RefreshHz);
        Assert.True(dto.Fullscreen);
        Assert.True(dto.Capped);
        Assert.Equal(90, dto.CapValue);
    }
}
