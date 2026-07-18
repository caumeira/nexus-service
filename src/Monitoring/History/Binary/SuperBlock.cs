using System;
using System.Buffers.Binary;
using System.IO;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Double-buffered, crash-safe header for the binary metrics store: two
/// fixed-size 4 KiB slots on disk, written alternately so a crash mid-write
/// can corrupt only the slot NOT currently trusted - the other slot, plus
/// its own Crc and generation counter, is what CreateOrOpen falls back to.
///
/// <para>Each slot holds: Magic | FormatVersion | ScalarRingCapacity |
/// PruneFloorSec | SourceFloorSec | Generation | Crc (covering everything
/// before it). CreateOrOpen reads both slots and trusts the one with the
/// higher Generation among those whose Magic/FormatVersion/Crc all check out
/// - a slot that fails any of those checks (including a capacity that no
/// longer matches what this run requested - see ScalarRingCapacity) is
/// treated exactly like a torn write, never like a format to migrate.</para>
///
/// <para>Persist always writes to whichever slot was NOT most recently
/// trusted, then flushes to disk before updating in-memory state - so a
/// crash during that write leaves the previously-good slot completely
/// intact for the next Open to fall back to.</para>
///
/// <para>This header is small and written rarely (only when the prune floor
/// advances, at most once per MetricsHistory.FlushSeconds), so it uses plain
/// FileStream I/O rather than RingFile's memory-mapped, lock-free design:
/// nothing here needs a concurrent lock-free reader, only the same
/// single-writer discipline the rest of the store already has - Open runs
/// once at startup, and Persist is only ever called from that same
/// writer.</para>
/// </summary>
internal sealed class SuperBlock : IDisposable
{
    private const uint Magic = 0x5342584E;
    private const uint FormatVersion = 1;
    private const int SlotSize = 4096;

    // magic(4) + formatVersion(4) + scalarRingCapacity(8) + pruneFloorSec(8)
    // + sourceFloorSec(8) + generation(8) - Crc immediately follows, at this
    // offset within the slot. Internal (not private) so tests can find the
    // Crc field precisely to simulate a corrupted header.
    internal const int HeaderBytes = 4 + 4 + 8 + 8 + 8 + 8;
    private const int CrcBytes = 4;

    private const long UnsetSourceFloor = long.MinValue;
    private const long NoPruneFloor = long.MinValue;

    private readonly FileStream _file;
    private int _activeSlot;
    private ulong _generation;

    /// <summary>The scalar ring capacity this header was created for. A
    /// reopen that requests a different capacity treats every existing slot
    /// as invalid (see the class doc) rather than reinterpreting floors that
    /// were computed against a differently-sized ring.</summary>
    public long ScalarRingCapacity { get; }

    /// <summary>The last persisted prune floor, or <see cref="long.MinValue"/>
    /// if none has ever been set (accept every timestamp).</summary>
    public long PruneFloorSec { get; private set; }

    /// <summary>The last persisted source floor (populated by a later phase's
    /// rollup rebuild guard; Phase 1 never sets this, only carries it
    /// through), or null if never set.</summary>
    public long? SourceFloorSec { get; private set; }

    /// <summary>Byte offset of the slot Persist most recently wrote to (the
    /// current highest-generation slot). Test-only: lets a test corrupt a
    /// real, specific on-disk slot to simulate a torn or bit-rotted header
    /// without duplicating the active-slot bookkeeping above.</summary>
    internal long ActiveSlotOffsetForTests => _activeSlot * (long)SlotSize;

    private SuperBlock(FileStream file, int activeSlot, ulong generation, long scalarRingCapacity, long pruneFloorSec, long? sourceFloorSec)
    {
        _file = file;
        _activeSlot = activeSlot;
        _generation = generation;
        ScalarRingCapacity = scalarRingCapacity;
        PruneFloorSec = pruneFloorSec;
        SourceFloorSec = sourceFloorSec;
    }

    /// <summary>Opens the header at <paramref name="path"/>, creating it if
    /// missing. If neither slot validates for <paramref name="scalarRingCapacity"/>
    /// (a fresh file, a torn file, or one written for a different capacity),
    /// formats slot 0 fresh with no prune floor and no source floor.</summary>
    public static SuperBlock CreateOrOpen(string path, long scalarRingCapacity)
    {
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (file.Length < SlotSize * 2L)
        {
            file.SetLength(SlotSize * 2L);
        }

        var slotA = ReadSlot(file, 0, scalarRingCapacity);
        var slotB = ReadSlot(file, 1, scalarRingCapacity);

        SlotContents? chosen = null;
        var chosenIndex = 0;
        if (slotA is { } a && slotB is { } b)
        {
            chosenIndex = a.Generation >= b.Generation ? 0 : 1;
            chosen = a.Generation >= b.Generation ? a : b;
        }
        else if (slotA is { } onlyA)
        {
            chosenIndex = 0;
            chosen = onlyA;
        }
        else if (slotB is { } onlyB)
        {
            chosenIndex = 1;
            chosen = onlyB;
        }

        if (chosen is { } c)
        {
            return new SuperBlock(file, chosenIndex, c.Generation, scalarRingCapacity, c.PruneFloorSec, c.SourceFloorSec);
        }

        // Neither slot validates: format slot 0 as generation 1. Starting
        // "active" at slot 1 makes the Persist call below target slot 0, the
        // same alternation rule every later Persist follows.
        var fresh = new SuperBlock(file, activeSlot: 1, generation: 0, scalarRingCapacity, NoPruneFloor, sourceFloorSec: null);
        fresh.Persist(NoPruneFloor, null);
        return fresh;
    }

    /// <summary>Bumps the generation, writes the alternate slot (never the
    /// one currently trusted), and flushes to disk before returning -
    /// matching the crash-safety guarantee in the class doc.</summary>
    public void Persist(long pruneFloorSec, long? sourceFloorSec)
    {
        var targetSlot = _activeSlot == 0 ? 1 : 0;
        var nextGeneration = _generation + 1;
        WriteSlot(targetSlot, nextGeneration, pruneFloorSec, sourceFloorSec);
        _file.Flush(flushToDisk: true);

        _activeSlot = targetSlot;
        _generation = nextGeneration;
        PruneFloorSec = pruneFloorSec;
        SourceFloorSec = sourceFloorSec;
    }

    private void WriteSlot(int slotIndex, ulong generation, long pruneFloorSec, long? sourceFloorSec)
    {
        Span<byte> full = stackalloc byte[HeaderBytes + CrcBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(full[0..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(full[4..], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(full[8..], ScalarRingCapacity);
        BinaryPrimitives.WriteInt64LittleEndian(full[16..], pruneFloorSec);
        BinaryPrimitives.WriteInt64LittleEndian(full[24..], sourceFloorSec ?? UnsetSourceFloor);
        BinaryPrimitives.WriteUInt64LittleEndian(full[32..], generation);
        var crc = Crc32.Compute(full[..HeaderBytes]);
        BinaryPrimitives.WriteUInt32LittleEndian(full[HeaderBytes..], crc);

        _file.Seek(slotIndex * (long)SlotSize, SeekOrigin.Begin);
        _file.Write(full);
    }

    private static SlotContents? ReadSlot(FileStream file, int slotIndex, long expectedCapacity)
    {
        Span<byte> full = stackalloc byte[HeaderBytes + CrcBytes];
        file.Seek(slotIndex * (long)SlotSize, SeekOrigin.Begin);
        if (file.Read(full) != full.Length)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(full[0..]) != Magic)
        {
            return null;
        }
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(full[HeaderBytes..]);
        if (Crc32.Compute(full[..HeaderBytes]) != storedCrc)
        {
            return null;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(full[4..]) != FormatVersion)
        {
            return null;
        }

        var ringCapacity = BinaryPrimitives.ReadInt64LittleEndian(full[8..]);
        if (ringCapacity != expectedCapacity)
        {
            // A capacity change means RingFile is about to reformat the
            // ring file too (see RingFile's own capacity-mismatch handling)
            // - a floor computed against the old capacity no longer means
            // anything, so this slot is treated the same as a torn write.
            return null;
        }

        var pruneFloorSec = BinaryPrimitives.ReadInt64LittleEndian(full[16..]);
        var sourceFloorRaw = BinaryPrimitives.ReadInt64LittleEndian(full[24..]);
        var generation = BinaryPrimitives.ReadUInt64LittleEndian(full[32..]);

        return new SlotContents(
            pruneFloorSec,
            sourceFloorRaw == UnsetSourceFloor ? null : sourceFloorRaw,
            generation);
    }

    private readonly record struct SlotContents(long PruneFloorSec, long? SourceFloorSec, ulong Generation);

    public void Dispose() => _file.Dispose();
}
