using Nexus.Service.Activity;

namespace Nexus.Service.Tests.Activity;

public sealed class IconDiskCacheTests : IDisposable
{
    private readonly string _root;

    public IconDiskCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-icon-cache-tests-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private IconDiskCache NewCache() => new(_root);

    private static readonly byte[] Png1 = { 1, 2, 3, 4 };
    private static readonly byte[] Png2 = { 5, 6, 7, 8, 9 };

    [Fact]
    public void TryGet_OnEmptyCache_ReturnsNull()
    {
        var cache = NewCache();

        var result = cache.TryGet("app-1", "C:\\Start Menu\\app.lnk", DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void Store_ThenTryGet_WithSameSource_ReturnsTheBytes()
    {
        var cache = NewCache();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        cache.Store("app-1", "C:\\lnk\\app.lnk", mtime, Png1);
        var result = cache.TryGet("app-1", "C:\\lnk\\app.lnk", mtime);

        Assert.Equal(Png1, result);
    }

    [Fact]
    public void TryGet_WithDifferentWriteTime_IsStale_ReturnsNull()
    {
        var cache = NewCache();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        cache.Store("app-1", "C:\\lnk\\app.lnk", mtime, Png1);

        var result = cache.TryGet("app-1", "C:\\lnk\\app.lnk", mtime.AddMinutes(1));

        Assert.Null(result);
    }

    [Fact]
    public void TryGet_WithDifferentSourcePath_IsStale_ReturnsNull()
    {
        var cache = NewCache();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        cache.Store("app-1", "C:\\lnk\\app.lnk", mtime, Png1);

        var result = cache.TryGet("app-1", "C:\\lnk\\moved-app.lnk", mtime);

        Assert.Null(result);
    }

    [Fact]
    public void Store_Overwrites_PreviousEntry_ForSameTargetId()
    {
        var cache = NewCache();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        cache.Store("app-1", "C:\\lnk\\app.lnk", mtime, Png1);

        var mtime2 = mtime.AddDays(1);
        cache.Store("app-1", "C:\\lnk\\app.lnk", mtime2, Png2);

        Assert.Null(cache.TryGet("app-1", "C:\\lnk\\app.lnk", mtime));
        Assert.Equal(Png2, cache.TryGet("app-1", "C:\\lnk\\app.lnk", mtime2));
    }

    [Fact]
    public void KeyFor_IsStable_AndDistinctPerTargetId()
    {
        Assert.Equal(IconDiskCache.KeyFor("app-1"), IconDiskCache.KeyFor("app-1"));
        Assert.NotEqual(IconDiskCache.KeyFor("app-1"), IconDiskCache.KeyFor("app-2"));
    }

    [Fact]
    public void PruneStale_RemovesEntriesOlderThanMaxAge()
    {
        var cache = NewCache();
        cache.Store("old-app", "C:\\lnk\\old.lnk", DateTime.UtcNow, Png1);
        var oldPngPath = Path.Combine(_root, IconDiskCache.KeyFor("old-app") + ".png");
        var oldMetaPath = Path.Combine(_root, IconDiskCache.KeyFor("old-app") + ".meta.json");
        File.SetLastWriteTimeUtc(oldPngPath, DateTime.UtcNow.AddDays(-40));

        var freshMtime = DateTime.UtcNow;
        cache.Store("fresh-app", "C:\\lnk\\fresh.lnk", freshMtime, Png2);

        var removed = cache.PruneStale(TimeSpan.FromDays(30), maxEntries: 500);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldPngPath));
        Assert.False(File.Exists(oldMetaPath));
        Assert.NotNull(cache.TryGet("fresh-app", "C:\\lnk\\fresh.lnk", freshMtime));
    }

    [Fact]
    public void PruneStale_OverCountCap_RemovesOldestFirst()
    {
        var cache = NewCache();
        for (var i = 0; i < 5; i++)
        {
            cache.Store($"app-{i}", $"C:\\lnk\\app{i}.lnk", DateTime.UtcNow, Png1);
            var pngPath = Path.Combine(_root, IconDiskCache.KeyFor($"app-{i}") + ".png");
            // Space the write times out so the ordering to prune is deterministic.
            File.SetLastWriteTimeUtc(pngPath, DateTime.UtcNow.AddMinutes(-5 + i));
        }

        var removed = cache.PruneStale(TimeSpan.FromDays(30), maxEntries: 3);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(_root, IconDiskCache.KeyFor("app-0") + ".png")));
        Assert.False(File.Exists(Path.Combine(_root, IconDiskCache.KeyFor("app-1") + ".png")));
        Assert.True(File.Exists(Path.Combine(_root, IconDiskCache.KeyFor("app-4") + ".png")));
    }

    [Fact]
    public void PruneStale_RemovesOrphanedMetaFile_WithNoMatchingPng()
    {
        var cache = NewCache();
        Directory.CreateDirectory(_root);
        var orphanMeta = Path.Combine(_root, "deadbeef.meta.json");
        File.WriteAllText(orphanMeta, "{}");

        cache.PruneStale(TimeSpan.FromDays(30), maxEntries: 500);

        Assert.False(File.Exists(orphanMeta));
    }

    [Fact]
    public void PruneStale_OnMissingRoot_ReturnsZero_DoesNotThrow()
    {
        var cache = NewCache();

        var removed = cache.PruneStale(TimeSpan.FromDays(30), maxEntries: 500);

        Assert.Equal(0, removed);
    }
}
