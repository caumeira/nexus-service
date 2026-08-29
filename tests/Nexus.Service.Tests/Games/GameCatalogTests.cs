using Nexus.Service.Games;

namespace Nexus.Service.Tests.Games;

public class GameCatalogTests
{
    [Theory]
    [InlineData("steam", "1091500", "Cyberpunk 2077", "steam:1091500")]
    [InlineData("epic", "", "Fortnite", "epic:fortnite")]
    [InlineData("ubisoft", "", "Assassin's Creed Valhalla", "ubisoft:assassinscreedvalhalla")]
    public void BuildGameKey_MatchesTheDecidedFormat(string store, string appId, string name, string expected)
    {
        Assert.Equal(expected, GameCatalog.BuildGameKey(store, appId, name));
    }

    [Fact]
    public void Slugify_LowercasesAndStripsNonAlnum()
    {
        Assert.Equal("halflife2", GameCatalog.Slugify("Half-Life 2"));
    }

    // dirKey values run through InstalledGameCollectors.CanonicalDirKey, the
    // same normalization the real index applies, so the comparison against
    // TryResolveAgainst's own Path.GetFullPath-normalized exePath lines up
    // on every OS this suite runs on (Path.GetFullPath treats a bare
    // "C:\..." string very differently on Windows vs. Unix, but both sides
    // going through the identical transform keeps the relative prefix
    // relationship intact either way).
    [Fact]
    public void TryResolveAgainst_LongestInstallDirPrefix_Wins()
    {
        var outer = new GameIdentity("steam:1", "Outer", "steam", "1");
        var inner = new GameIdentity("epic:inner", "Inner", "epic", "");
        var index = new List<(string DirKey, GameIdentity Game)>
        {
            (InstalledGameCollectors.CanonicalDirKey(@"C:\Games"), outer),
            (InstalledGameCollectors.CanonicalDirKey(@"C:\Games\Inner"), inner),
        };

        var resolved = GameCatalog.TryResolveAgainst(index, @"C:\Games\Inner\game.exe", out var identity);

        Assert.True(resolved);
        Assert.Equal("epic:inner", identity.GameKey);
    }

    [Fact]
    public void TryResolveAgainst_CaseInsensitive()
    {
        var game = new GameIdentity("steam:1", "Game", "steam", "1");
        var index = new List<(string DirKey, GameIdentity Game)>
        {
            (InstalledGameCollectors.CanonicalDirKey(@"C:\Games\Title"), game),
        };

        var resolved = GameCatalog.TryResolveAgainst(index, @"c:\games\title\GAME.EXE", out var identity);

        Assert.True(resolved);
        Assert.Equal("steam:1", identity.GameKey);
    }

    [Fact]
    public void TryResolveAgainst_ExeOutsideEveryInstallDir_ReturnsFalse()
    {
        var game = new GameIdentity("steam:1", "Game", "steam", "1");
        var index = new List<(string DirKey, GameIdentity Game)>
        {
            (InstalledGameCollectors.CanonicalDirKey(@"C:\Games\Title"), game),
        };

        var resolved = GameCatalog.TryResolveAgainst(index, @"C:\Windows\explorer.exe", out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveAgainst_EmptyExePath_ReturnsFalse()
    {
        var resolved = GameCatalog.TryResolveAgainst(new List<(string, GameIdentity)>(), "", out _);
        Assert.False(resolved);
    }
}
