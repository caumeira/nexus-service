using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// AppNameDictionary's own id bookkeeping, decoupled from AppUsageStore:
/// sequential id assignment, case-insensitive dedup with first-seen casing
/// kept, id stability across a reopen, and the torn-trailing-record recovery
/// a crash mid-registration leaves behind - the app-name equivalent of
/// EntityRegistryTests.
/// </summary>
public class AppNameDictionaryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppNameDictionaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appnamedict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "apps.dict");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RegisterOrGet_NewNames_AssignsSequentialIds()
    {
        using var dict = AppNameDictionary.Open(_path);

        Assert.Equal(0, dict.RegisterOrGet("chrome.exe"));
        Assert.Equal(1, dict.RegisterOrGet("steam.exe"));
        Assert.Equal(2, dict.Names.Count);
    }

    [Fact]
    public void RegisterOrGet_SameNameDifferentCase_ReturnsTheSameId()
    {
        using var dict = AppNameDictionary.Open(_path);

        var first = dict.RegisterOrGet("Chrome.exe");
        var second = dict.RegisterOrGet("chrome.exe");
        var third = dict.RegisterOrGet("CHROME.EXE");

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Single(dict.Names);
    }

    [Fact]
    public void RegisterOrGet_WithAnOrdinalComparer_TreatsDifferentCasingAsDistinctNames()
    {
        using var dict = AppNameDictionary.Open(_path, StringComparer.Ordinal);

        var first = dict.RegisterOrGet("Slack");
        var second = dict.RegisterOrGet("slack");

        Assert.NotEqual(first, second);
        Assert.Equal(2, dict.Names.Count);
        Assert.Null(dict.TryGetId("SLACK"));
    }

    [Fact]
    public void RegisterOrGet_KeepsTheFirstSeenCasing()
    {
        using var dict = AppNameDictionary.Open(_path);

        var id = dict.RegisterOrGet("Chrome.exe");
        dict.RegisterOrGet("chrome.exe");
        dict.RegisterOrGet("CHROME.EXE");

        Assert.Equal("Chrome.exe", dict.GetName(id));
    }

    [Fact]
    public void TryGetId_UnknownName_ReturnsNull_WithoutRegisteringIt()
    {
        using var dict = AppNameDictionary.Open(_path);

        Assert.Null(dict.TryGetId("never-seen.exe"));
        Assert.Empty(dict.Names);
    }

    [Fact]
    public void TryGetId_MatchesCaseInsensitively()
    {
        using var dict = AppNameDictionary.Open(_path);
        dict.RegisterOrGet("Chrome.exe");

        Assert.Equal(0, dict.TryGetId("CHROME.EXE"));
    }

    [Fact]
    public void Reopen_RecoversEveryName_WithStableIds()
    {
        using (var dict = AppNameDictionary.Open(_path))
        {
            dict.RegisterOrGet("chrome.exe");
            dict.RegisterOrGet("steam.exe");
        }

        using var reopened = AppNameDictionary.Open(_path);

        Assert.Equal(2, reopened.Names.Count);
        Assert.Equal(0, reopened.TryGetId("chrome.exe"));
        Assert.Equal(1, reopened.TryGetId("steam.exe"));
        Assert.Equal("chrome.exe", reopened.Names[0]);
        Assert.Equal("steam.exe", reopened.Names[1]);
    }

    [Fact]
    public void Reopen_WithATornTrailingRecord_KeepsEarlierEntries_AndTruncatesTheTornOne()
    {
        using (var dict = AppNameDictionary.Open(_path))
        {
            dict.RegisterOrGet("chrome.exe");
        }

        // Simulate a crash mid-append: a second record's header claims more
        // name bytes than actually follow it.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(100)); // nameLen: claims 100 bytes
            fs.Write(new byte[] { 1, 2, 3 });      // far short of 100 bytes
        }

        using var reopened = AppNameDictionary.Open(_path);

        Assert.Single(reopened.Names);
        Assert.Equal(0, reopened.TryGetId("chrome.exe"));

        // The torn tail was truncated away, so a fresh registration lands at
        // the next clean index rather than colliding with the debris.
        Assert.Equal(1, reopened.RegisterOrGet("steam.exe"));
    }
}
