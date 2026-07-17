using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessIconCacheTests
{
    [Fact]
    public void TryGet_OnEmptyCache_Misses()
    {
        var cache = new ProcessIconCache();

        var hit = cache.TryGet("C:\\app.exe", out var bytes);

        Assert.False(hit);
        Assert.Empty(bytes);
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheStoredBytes()
    {
        var cache = new ProcessIconCache();
        var png = new byte[] { 1, 2, 3 };

        cache.Set("C:\\app.exe", png);

        var hit = cache.TryGet("C:\\app.exe", out var bytes);
        Assert.True(hit);
        Assert.Equal(png, bytes);
    }

    [Fact]
    public void Set_CachesAnEmptyResultToo_AsANegativeHit()
    {
        var cache = new ProcessIconCache();

        cache.Set("C:\\unresolvable.exe", System.Array.Empty<byte>());

        var hit = cache.TryGet("C:\\unresolvable.exe", out var bytes);
        Assert.True(hit);
        Assert.Empty(bytes);
    }

    [Fact]
    public void Set_OnAnExistingKey_ReplacesTheValue()
    {
        var cache = new ProcessIconCache();
        cache.Set("C:\\app.exe", new byte[] { 1 });

        cache.Set("C:\\app.exe", new byte[] { 2, 2 });

        cache.TryGet("C:\\app.exe", out var bytes);
        Assert.Equal(new byte[] { 2, 2 }, bytes);
    }

    [Fact]
    public void Cache_EvictsTheOldestEntry_PastTwoHundredDistinctPaths()
    {
        var cache = new ProcessIconCache();
        for (var i = 0; i < 200; i++)
        {
            cache.Set($"C:\\app{i}.exe", new byte[] { 1 });
        }

        cache.Set("C:\\app200.exe", new byte[] { 1 });

        Assert.False(cache.TryGet("C:\\app0.exe", out _));
        Assert.True(cache.TryGet("C:\\app200.exe", out _));
    }

    [Fact]
    public void Cache_KeepsAdmittingNewEntries_WellPastTheFirstEviction()
    {
        // Repro for a dictionary-iteration-order eviction scheme: once one
        // eviction has happened, a naive "remove the first enumerated key"
        // approach can start evicting every newly inserted key immediately,
        // because the freed dictionary slot is what a new key lands in and
        // iterates first. Insert well past the cap and check every recent
        // entry, not just the one right after the first eviction.
        var cache = new ProcessIconCache();
        for (var i = 0; i < 260; i++)
        {
            cache.Set($"C:\\app{i}.exe", new byte[] { (byte)(i % 256) });
        }

        for (var i = 240; i < 260; i++)
        {
            var hit = cache.TryGet($"C:\\app{i}.exe", out var bytes);
            Assert.True(hit, $"C:\\app{i}.exe should still be cached");
            Assert.Equal((byte)(i % 256), bytes[0]);
        }
    }

    [Fact]
    public void TryGet_RefreshesRecency_SoAnOldButReAccessedEntrySurvivesEviction()
    {
        var cache = new ProcessIconCache();
        cache.Set("C:\\kept.exe", new byte[] { 1 });
        for (var i = 0; i < 199; i++)
        {
            cache.Set($"C:\\filler{i}.exe", new byte[] { 1 });
        }

        cache.TryGet("C:\\kept.exe", out _); // refresh: now the most recently used

        for (var i = 199; i < 210; i++)
        {
            cache.Set($"C:\\newer{i}.exe", new byte[] { 1 });
        }

        Assert.True(cache.TryGet("C:\\kept.exe", out _));
        Assert.False(cache.TryGet("C:\\filler0.exe", out _)); // never refreshed, evicted first
    }
}
