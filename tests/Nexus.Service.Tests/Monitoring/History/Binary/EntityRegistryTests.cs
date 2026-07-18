using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// EntityRegistry's own id-&gt;ring-index bookkeeping, decoupled from
/// GpuRingStore/FanRingStore: sequential index assignment, the capacity
/// bound gpu/fan entities share, key stability across a reopen (the pool of
/// per-entity ring files depends on ring index never changing for a given
/// id), and the torn-trailing-record recovery a crash mid-registration
/// leaves behind.
/// </summary>
public class EntityRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public EntityRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-entityregistry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "entities.reg");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RegisterOrGet_NewIds_AssignsSequentialIndices()
    {
        using var registry = EntityRegistry.Open(_path, capacity: 32);

        Assert.Equal(0, registry.RegisterOrGet("gpu-0", "RTX 5080"));
        Assert.Equal(1, registry.RegisterOrGet("gpu-1", "RX 7900"));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void RegisterOrGet_SameId_ReturnsTheSameIndex_WithoutGrowingTheRegistry()
    {
        using var registry = EntityRegistry.Open(_path, capacity: 32);

        var first = registry.RegisterOrGet("gpu-0", "RTX 5080");
        var second = registry.RegisterOrGet("gpu-0", "RTX 5080");

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RegisterOrGet_PastCapacity_ReturnsNull_WithoutEvictingExistingEntities()
    {
        using var registry = EntityRegistry.Open(_path, capacity: 2);

        registry.RegisterOrGet("gpu-0", "A");
        registry.RegisterOrGet("gpu-1", "B");
        var overflow = registry.RegisterOrGet("gpu-2", "C");

        Assert.Null(overflow);
        Assert.Equal(2, registry.Count);
        Assert.Equal(0, registry.RegisterOrGet("gpu-0", "A")); // still resolvable
    }

    [Fact]
    public void Reopen_RecoversEveryEntity_WithStableIndices()
    {
        using (var registry = EntityRegistry.Open(_path, capacity: 32))
        {
            registry.RegisterOrGet("gpu-0", "RTX 5080");
            registry.RegisterOrGet("gpu-1", "RX 7900");
        }

        using var reopened = EntityRegistry.Open(_path, capacity: 32);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(0, reopened.RegisterOrGet("gpu-0", "RTX 5080"));
        Assert.Equal(1, reopened.RegisterOrGet("gpu-1", "RX 7900"));
        Assert.Equal(("gpu-0", "RTX 5080"), reopened.Entries[0]);
        Assert.Equal(("gpu-1", "RX 7900"), reopened.Entries[1]);
    }

    [Fact]
    public void Reopen_WithATornTrailingRecord_KeepsEarlierEntries_AndTruncatesTheTornOne()
    {
        using (var registry = EntityRegistry.Open(_path, capacity: 32))
        {
            registry.RegisterOrGet("gpu-0", "RTX 5080");
        }

        // Simulate a crash mid-append: a second record's header claims more
        // id bytes than actually follow it.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(100)); // idLen: claims 100 bytes
            fs.Write(BitConverter.GetBytes(0));   // nameLen
            fs.Write(new byte[] { 1, 2, 3 });      // far short of 100 bytes
        }

        using var reopened = EntityRegistry.Open(_path, capacity: 32);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(0, reopened.RegisterOrGet("gpu-0", "RTX 5080"));

        // The torn tail was truncated away, so a fresh registration lands
        // at the next clean index rather than colliding with the debris.
        Assert.Equal(1, reopened.RegisterOrGet("gpu-1", "RX 7900"));
    }
}
