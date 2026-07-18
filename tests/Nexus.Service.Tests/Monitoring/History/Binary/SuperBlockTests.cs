using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// Exercises SuperBlock's double-buffered recovery directly: a fresh file,
/// persist-then-reopen, and the "highest VALID generation wins, a corrupted
/// newer generation is skipped" recovery rule the shared IMetricsHistoryStore
/// specs have no way to exercise (SQLite has no equivalent header).
/// </summary>
public class SuperBlockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SuperBlockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-superblock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "super");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateOrOpen_OnAFreshPath_HasNoPruneFloorAndNoSourceFloor()
    {
        using var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 604_800);

        Assert.Equal(long.MinValue, sb.PruneFloorSec);
        Assert.Null(sb.SourceFloorSec);
        Assert.Equal(604_800, sb.ScalarRingCapacity);
    }

    [Fact]
    public void Persist_ThenReopen_RecoversTheLastPersistedFloors()
    {
        using (var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100))
        {
            sb.Persist(pruneFloorSec: 1000, sourceFloorSec: 500);
        }

        using var reopened = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100);
        Assert.Equal(1000, reopened.PruneFloorSec);
        Assert.Equal(500, reopened.SourceFloorSec);
    }

    [Fact]
    public void Persist_AlternatesSlots_SoASecondPersistDoesNotDependOnTheFirst()
    {
        using var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100);

        sb.Persist(pruneFloorSec: 100, sourceFloorSec: null);
        var firstOffset = sb.ActiveSlotOffsetForTests;
        sb.Persist(pruneFloorSec: 200, sourceFloorSec: null);
        var secondOffset = sb.ActiveSlotOffsetForTests;

        Assert.NotEqual(firstOffset, secondOffset);
        Assert.Equal(200, sb.PruneFloorSec);
    }

    [Fact]
    public void Open_WithACorruptedHigherGeneration_FallsBackToTheOlderValidGeneration()
    {
        long corruptOffset;
        using (var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100))
        {
            sb.Persist(pruneFloorSec: 500, sourceFloorSec: null);  // generation 2
            sb.Persist(pruneFloorSec: 1000, sourceFloorSec: null); // generation 3: higher, most recent
            corruptOffset = sb.ActiveSlotOffsetForTests;           // the slot holding generation 3 / 1000
        }

        // Corrupt the Crc field of the higher-generation slot only - the
        // magic/version/fields all still read as plausible, but the header
        // no longer checksums, so it must be rejected exactly like a torn
        // write.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(corruptOffset + SuperBlock.HeaderBytes, SeekOrigin.Begin);
            fs.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        using var reopened = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100);

        // Recovers generation 2's value (500), never generation 3's (1000):
        // the highest-generation slot is corrupt, so the next-highest VALID
        // one wins.
        Assert.Equal(500, reopened.PruneFloorSec);
    }

    [Fact]
    public void Open_WithBothSlotsCorrupted_BehavesLikeAFreshFile()
    {
        using (var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100))
        {
            sb.Persist(pruneFloorSec: 500, sourceFloorSec: null);
        }

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Write(new byte[8192]); // zero out both 4 KiB slots entirely
        }

        using var reopened = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100);
        Assert.Equal(long.MinValue, reopened.PruneFloorSec);
        Assert.Null(reopened.SourceFloorSec);
    }

    [Fact]
    public void Open_WithACapacityMismatch_StartsFreshRatherThanMisreadingTheOldFloor()
    {
        using (var sb = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 100))
        {
            sb.Persist(pruneFloorSec: 1000, sourceFloorSec: null);
        }

        using var reopened = SuperBlock.CreateOrOpen(_path, scalarRingCapacity: 200);
        Assert.Equal(long.MinValue, reopened.PruneFloorSec);
        Assert.Equal(200, reopened.ScalarRingCapacity);
    }
}
