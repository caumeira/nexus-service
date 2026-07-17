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
}
