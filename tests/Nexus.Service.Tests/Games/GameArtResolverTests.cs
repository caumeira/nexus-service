using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class GameArtResolverTests
{
    [Theory]
    [InlineData("steam:2473350", true, 2473350)]
    [InlineData("steam:427520", true, 427520)]
    [InlineData("epic:sludgelife", false, 0)]
    [InlineData("ubisoft:farcry", false, 0)]
    [InlineData("steam:", false, 0)]
    [InlineData("steam:0", false, 0)]
    [InlineData("steam:notanumber", false, 0)]
    public void TryParseSteamAppId_AcceptsOnlyASteamKeyWithAPositiveId(string gameKey, bool expected, int expectedAppId)
    {
        Assert.Equal(expected, GameArtResolver.TryParseSteamAppId(gameKey, out var appId));
        Assert.Equal(expectedAppId, appId);
    }

    [Fact]
    public void PickGameExecutable_PrefersTheOneNamedAfterTheInstallFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SludgeLife-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            // The launcher is deliberately the larger file: the name match must
            // win over the size fallback.
            File.WriteAllBytes(Path.Combine(dir, "UnityCrashHandler64.exe"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(dir, "SludgeLife.exe"), new byte[16]);

            Assert.Equal("SludgeLife.exe", Path.GetFileName(GameArtResolver.PickGameExecutable(dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PickGameExecutable_FallsBackToTheLargestExecutable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-art-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "crashpad.exe"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "TheGame.exe"), new byte[4096]);

            Assert.Equal("TheGame.exe", Path.GetFileName(GameArtResolver.PickGameExecutable(dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PickGameExecutable_IsEmptyForAMissingOrExeFreeDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-art-" + Guid.NewGuid().ToString("n"));
        Assert.Equal("", GameArtResolver.PickGameExecutable(dir));

        Directory.CreateDirectory(dir);
        try { Assert.Equal("", GameArtResolver.PickGameExecutable(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LegacyCapsuleUrl_IsTheFallbackPathThatStillAnswersForOlderTitles()
    {
        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/427520/capsule_231x87.jpg",
            GameArtResolver.LegacyCapsuleUrl(427520));
    }
}
