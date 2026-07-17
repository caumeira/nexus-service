using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessHashCacheTests
{
    [Fact]
    public void TryGet_OnEmptyCache_Misses()
    {
        var cache = new ProcessHashCache();

        var hit = cache.TryGet("C:\\app.exe", 100, out var hash);

        Assert.False(hit);
        Assert.Equal("", hash);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheStoredHash()
    {
        var cache = new ProcessHashCache();

        cache.Set("C:\\app.exe", 100, "abc123");

        var hit = cache.TryGet("C:\\app.exe", 100, out var hash);
        Assert.True(hit);
        Assert.Equal("abc123", hash);
    }

    [Fact]
    public void TryGet_Misses_WhenTheMtimeDiffers()
    {
        var cache = new ProcessHashCache();
        cache.Set("C:\\app.exe", 100, "abc123");

        var hit = cache.TryGet("C:\\app.exe", 200, out _);

        Assert.False(hit);
    }

    [Fact]
    public void Set_OnAnExistingKey_ReplacesTheValue()
    {
        var cache = new ProcessHashCache();
        cache.Set("C:\\app.exe", 100, "first");

        cache.Set("C:\\app.exe", 100, "second");

        cache.TryGet("C:\\app.exe", 100, out var hash);
        Assert.Equal("second", hash);
    }

    [Fact]
    public void Cache_EvictsTheOldestEntry_PastOneHundredDistinctKeys()
    {
        var cache = new ProcessHashCache();
        for (var i = 0; i < 100; i++)
        {
            cache.Set($"C:\\app{i}.exe", 1, "h");
        }

        cache.Set("C:\\app100.exe", 1, "h");

        Assert.False(cache.TryGet("C:\\app0.exe", 1, out _));
        Assert.True(cache.TryGet("C:\\app100.exe", 1, out _));
    }

    [Fact]
    public void TryGet_RefreshesRecency_SoAnOldButReAccessedEntrySurvivesEviction()
    {
        var cache = new ProcessHashCache();
        cache.Set("C:\\kept.exe", 1, "h");
        for (var i = 0; i < 99; i++)
        {
            cache.Set($"C:\\filler{i}.exe", 1, "h");
        }

        cache.TryGet("C:\\kept.exe", 1, out _); // refresh: now the most recently used

        for (var i = 99; i < 110; i++)
        {
            cache.Set($"C:\\newer{i}.exe", 1, "h");
        }

        Assert.True(cache.TryGet("C:\\kept.exe", 1, out _));
        Assert.False(cache.TryGet("C:\\filler0.exe", 1, out _)); // never refreshed, evicted first
    }
}
