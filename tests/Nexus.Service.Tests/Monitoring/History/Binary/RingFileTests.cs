using System;
using System.IO;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History.Binary;

/// <summary>
/// Exercises RingFile's own crash-safety mechanism directly: wraparound,
/// the null/absent-adjacent prune-floor gate, and the torn-write/corruption
/// paths the shared IMetricsHistoryStore specs never touch (SQLite has no
/// equivalent failure mode to pin). Uses a tiny 4-byte opaque body and a
/// small capacity throughout - RingFile does not know or care what the
/// bytes mean, so there is no need for real scalar data to prove the
/// mechanism.
/// </summary>
public class RingFileTests : IDisposable
{
    private const int BodyLength = 4;

    private readonly string _dir;
    private readonly string _path;

    public RingFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-ringfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "test.ring");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] BodyOf(int value)
    {
        var bytes = new byte[BodyLength];
        BitConverter.TryWriteBytes(bytes, value);
        return bytes;
    }

    private static int ValueOf(ReadOnlySpan<byte> body) => BitConverter.ToInt32(body);

    [Fact]
    public void WriteSlot_ThenTryReadSlot_RoundTrips()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        ring.WriteSlot(5, BodyOf(42));

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.True(ring.TryReadSlot(5, read));
        Assert.Equal(42, ValueOf(read));
    }

    [Fact]
    public void TryReadSlot_NeverWritten_ReadsAsAbsent()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(ring.TryReadSlot(3, read));
    }

    [Fact]
    public void WriteSlot_PastCapacity_WrapsAndTheOldTimestampReadsAsAbsent()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        ring.WriteSlot(5, BodyOf(1));
        ring.WriteSlot(15, BodyOf(2)); // 15 mod 10 == 5 mod 10: the same physical slot

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(ring.TryReadSlot(5, read));
        Assert.True(ring.TryReadSlot(15, read));
        Assert.Equal(2, ValueOf(read));
    }

    [Fact]
    public void WriteSlot_AcrossTheExactCapacityBoundary_WrapsCorrectly()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        ring.WriteSlot(9, BodyOf(1));   // last slot index before wraparound repeats
        ring.WriteSlot(19, BodyOf(2));  // first ts that reuses slot 9's index (19 mod 10 == 9)

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(ring.TryReadSlot(9, read));
        Assert.True(ring.TryReadSlot(19, read));
        Assert.Equal(2, ValueOf(read));
    }

    [Fact]
    public void WriteSlot_SameTimestampTwice_ReplacesRatherThanCorrupting()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        ring.WriteSlot(5, BodyOf(1));
        ring.WriteSlot(5, BodyOf(2));

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.True(ring.TryReadSlot(5, read));
        Assert.Equal(2, ValueOf(read));
    }

    [Fact]
    public void PruneFloorSec_HidesOlderSlots_WithoutPhysicallyErasingThem()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);
        ring.WriteSlot(2, BodyOf(1));

        ring.PruneFloorSec = 3;
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(ring.TryReadSlot(2, read));

        // Lowering the floor again is not something a real caller does (the
        // floor only ever rises - see ScalarRingStore.RaisePruneFloor), but
        // doing it here proves the data was only hidden, not erased.
        ring.PruneFloorSec = 0;
        Assert.True(ring.TryReadSlot(2, read));
        Assert.Equal(1, ValueOf(read));
    }

    [Fact]
    public void TryReadSlotAtIndex_ReturnsTheStoredTimestamp_ForAWholeRingScan()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);
        ring.WriteSlot(7, BodyOf(99));

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.True(ring.TryReadSlotAtIndex(7, read, out var ts));
        Assert.Equal(7, ts);
        Assert.Equal(99, ValueOf(read));
    }

    [Fact]
    public void TryReadSlotAtIndex_NeverWritten_ReportsTheUnwrittenSentinel()
    {
        using var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength);

        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(ring.TryReadSlotAtIndex(3, read, out var ts));
        Assert.Equal(RingFile.UnwrittenStamp, ts);
    }

    [Fact]
    public void Reopen_WithATornLeadStamp_ReadsAsAbsent_NeverAsGarbage()
    {
        const long ts = 4;
        const long capacity = 10;
        int leadFileOffset;

        using (var ring = RingFile.CreateOrOpen(_path, capacity, BodyLength))
        {
            ring.WriteSlot(ts, BodyOf(123));
            ring.Flush();
            leadFileOffset = SlotFileOffset(ring, ts, capacity) + ring.LeadOffset;
        }

        // Simulate a crash between the body/Crc/trailing-stamp writes and
        // the final leading-stamp publish: revert only the lead bytes to
        // "never written". Everything else on disk still says ts=4.
        WriteRawBytes(leadFileOffset, BitConverter.GetBytes(RingFile.UnwrittenStamp));

        using var reopened = RingFile.CreateOrOpen(_path, capacity, BodyLength);
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(reopened.TryReadSlot(ts, read));
    }

    [Fact]
    public void Reopen_WithATornTrailStamp_ReadsAsAbsent_NeverAsGarbage()
    {
        const long ts = 4;
        const long capacity = 10;
        int trailFileOffset;

        using (var ring = RingFile.CreateOrOpen(_path, capacity, BodyLength))
        {
            ring.WriteSlot(ts, BodyOf(123));
            ring.Flush();
            trailFileOffset = SlotFileOffset(ring, ts, capacity) + ring.TrailOffset;
        }

        WriteRawBytes(trailFileOffset, BitConverter.GetBytes(RingFile.UnwrittenStamp));

        using var reopened = RingFile.CreateOrOpen(_path, capacity, BodyLength);
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(reopened.TryReadSlot(ts, read));
    }

    [Fact]
    public void Reopen_WithACorruptedCrc_ReadsAsAbsent_NeverAsGarbage()
    {
        const long ts = 4;
        const long capacity = 10;
        int crcFileOffset;

        using (var ring = RingFile.CreateOrOpen(_path, capacity, BodyLength))
        {
            ring.WriteSlot(ts, BodyOf(123));
            ring.Flush();
            crcFileOffset = SlotFileOffset(ring, ts, capacity) + ring.CrcOffset;
        }

        WriteRawBytes(crcFileOffset, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        using var reopened = RingFile.CreateOrOpen(_path, capacity, BodyLength);
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(reopened.TryReadSlot(ts, read));
    }

    [Fact]
    public void Reopen_WithACorruptedBody_ReadsAsAbsent_NeverAsGarbage()
    {
        const long ts = 4;
        const long capacity = 10;
        int bodyFileOffset;

        using (var ring = RingFile.CreateOrOpen(_path, capacity, BodyLength))
        {
            ring.WriteSlot(ts, BodyOf(123));
            ring.Flush();
            bodyFileOffset = SlotFileOffset(ring, ts, capacity) + ring.BodyOffset;
        }

        // The stamps still agree on ts=4, but the body no longer matches
        // the persisted Crc - this is the "torn disk write on a same-second
        // replace" scenario the class doc calls out as the reason a Crc
        // check exists on top of the stamp match.
        WriteRawBytes(bodyFileOffset, BodyOf(456));

        using var reopened = RingFile.CreateOrOpen(_path, capacity, BodyLength);
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(reopened.TryReadSlot(ts, read));
    }

    [Fact]
    public void CreateOrOpen_WithACapacityMismatch_ReformatsRatherThanMisreadingTheOldFile()
    {
        using (var ring = RingFile.CreateOrOpen(_path, capacity: 10, BodyLength))
        {
            ring.WriteSlot(4, BodyOf(123));
        }

        using var reopened = RingFile.CreateOrOpen(_path, capacity: 20, BodyLength);
        Span<byte> read = stackalloc byte[BodyLength];
        Assert.False(reopened.TryReadSlot(4, read));
        Assert.Equal(20, reopened.Capacity);
    }

    private static int SlotFileOffset(RingFile ring, long ts, long capacity)
    {
        var index = ((ts % capacity) + capacity) % capacity;
        return (int)(index * ring.Stride);
    }

    private void WriteRawBytes(int fileOffset, byte[] bytes)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(fileOffset, SeekOrigin.Begin);
        fs.Write(bytes);
    }
}
