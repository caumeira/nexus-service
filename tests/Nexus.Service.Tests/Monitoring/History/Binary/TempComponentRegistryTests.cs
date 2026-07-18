using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// TempComponentRegistry's own id-&gt;ring-index bookkeeping, mirroring
/// EntityRegistryTests for the one extra Kind field this registry carries:
/// sequential index assignment, the capacity bound, key stability across a
/// reopen, and the torn-trailing-record recovery a crash mid-registration
/// leaves behind.
/// </summary>
public class TempComponentRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TempComponentRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-tempcomponentregistry-" + Guid.NewGuid().ToString("N")[..8]);
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
        using var registry = TempComponentRegistry.Open(_path, capacity: 32);

        Assert.Equal(0, registry.RegisterOrGet("storage:serial1", "storage", "Samsung 990 Pro"));
        Assert.Equal(1, registry.RegisterOrGet("ram:0", "ram", "DIMM A2"));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void RegisterOrGet_SameId_ReturnsTheSameIndex_WithoutGrowingTheRegistry()
    {
        using var registry = TempComponentRegistry.Open(_path, capacity: 32);

        var first = registry.RegisterOrGet("ram:0", "ram", "DIMM A2");
        var second = registry.RegisterOrGet("ram:0", "ram", "DIMM A2");

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RegisterOrGet_PastCapacity_ReturnsNull_WithoutEvictingExistingEntities()
    {
        using var registry = TempComponentRegistry.Open(_path, capacity: 2);

        registry.RegisterOrGet("ram:0", "ram", "A");
        registry.RegisterOrGet("ram:1", "ram", "B");
        var overflow = registry.RegisterOrGet("ram:2", "ram", "C");

        Assert.Null(overflow);
        Assert.Equal(2, registry.Count);
        Assert.Equal(0, registry.RegisterOrGet("ram:0", "ram", "A")); // still resolvable
    }

    [Fact]
    public void Reopen_RecoversEveryEntity_WithStableIndicesAndKind()
    {
        using (var registry = TempComponentRegistry.Open(_path, capacity: 32))
        {
            registry.RegisterOrGet("storage:serial1", "storage", "Samsung 990 Pro");
            registry.RegisterOrGet("ram:0", "ram", "DIMM A2");
        }

        using var reopened = TempComponentRegistry.Open(_path, capacity: 32);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(0, reopened.RegisterOrGet("storage:serial1", "storage", "Samsung 990 Pro"));
        Assert.Equal(1, reopened.RegisterOrGet("ram:0", "ram", "DIMM A2"));
        Assert.Equal(("storage:serial1", "storage", "Samsung 990 Pro"), reopened.Entries[0]);
        Assert.Equal(("ram:0", "ram", "DIMM A2"), reopened.Entries[1]);
    }

    [Fact]
    public void Reopen_WithATornTrailingRecord_KeepsEarlierEntries_AndTruncatesTheTornOne()
    {
        using (var registry = TempComponentRegistry.Open(_path, capacity: 32))
        {
            registry.RegisterOrGet("ram:0", "ram", "DIMM A2");
        }

        // Simulate a crash mid-append: a second record's header claims more
        // id bytes than actually follow it.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(BitConverter.GetBytes(100)); // idLen: claims 100 bytes
            fs.Write(BitConverter.GetBytes(0));   // kindLen
            fs.Write(BitConverter.GetBytes(0));   // nameLen
            fs.Write(new byte[] { 1, 2, 3 });      // far short of 100 bytes
        }

        using var reopened = TempComponentRegistry.Open(_path, capacity: 32);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(0, reopened.RegisterOrGet("ram:0", "ram", "DIMM A2"));

        // The torn tail was truncated away, so a fresh registration lands
        // at the next clean index rather than colliding with the debris.
        Assert.Equal(1, reopened.RegisterOrGet("ram:1", "ram", "DIMM A3"));
    }
}
