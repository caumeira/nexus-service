using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Generic memory-mapped, fixed-stride, round-robin ring: <see
/// cref="Capacity"/> slots of a caller-declared body size, addressed by
/// <c>ts mod Capacity</c>. This is the crash-safe primitive every 1Hz series
/// in the binary metrics store is built on (the 1Hz scalar ring today; later
/// phases layer per-entity gpu/fan/temperature-component rings on the same
/// primitive) - RingFile itself has no notion of what the body bytes mean,
/// only that a slot's body is either fully and correctly written, or the
/// slot reads as absent.
///
/// <para><b>On-disk layout.</b> The file is exactly <c>Capacity * Stride</c>
/// bytes: <c>Capacity</c> fixed-size slots back to back, no header. One
/// slot is <c>LeadStamp(8) | body(BodyLength) | Crc(4) | TrailStamp(8)</c>.
/// LeadStamp and TrailStamp both hold the epoch-second timestamp the slot
/// currently belongs to; Crc covers exactly the body bytes.</para>
///
/// <para><b>Crash-safe write protocol.</b> A writer publishes a slot in this
/// order: body, then Crc, then TrailStamp, then - last - LeadStamp via
/// <see cref="Volatile.Write(ref long, long)"/> (a release fence). A reader
/// checks LeadStamp first via <see cref="Volatile.Read(ref long)"/> (an
/// acquire fence) and only trusts the rest of the slot if LeadStamp matches
/// the timestamp it asked for. Because LeadStamp is both physically first in
/// the slot and temporally LAST to be written, an acquire read of it that
/// matches is a guarantee (per the .NET memory model) that the body/Crc/
/// TrailStamp writes that preceded it in program order are also visible -
/// so a reader never needs its own lock to see a fully-written slot, and a
/// process that crashes mid-write leaves LeadStamp holding its old value,
/// which fails the match and reads as absent rather than as a torn record.
///
/// The Crc exists for a narrower case the stamps alone cannot catch: a
/// crash during the OS's flush of a dirty page can tear a write to disk
/// between two versions of the SAME timestamp (a same-second replace whose
/// old and new LeadStamp/TrailStamp values are identical), which would
/// otherwise pass the stamp check with a body that is a mix of old and new
/// bytes. TrailStamp itself does not need its own release fence: everything
/// before the final Volatile.Write is already ordered by that one release,
/// per the same argument above.</para>
///
/// <para><b>Absent vs corrupt.</b> TryReadSlot/TryReadSlotAtIndex return
/// false for every case where the slot cannot be trusted for the requested
/// timestamp: never written, wrapped around to a different timestamp, below
/// the prune floor, or failing the Crc/stamp check. There is no distinct
/// "corrupt" signal - a corrupt slot is indistinguishable from, and handled
/// identically to, a real gap in the data.</para>
///
/// <para><b>Fresh vs reopened file.</b> A brand-new ring file is formatted
/// with every slot's LeadStamp/TrailStamp set to <see cref="UnwrittenStamp"/>
/// (<see cref="long.MinValue"/>), a value no real epoch-second timestamp can
/// equal. TryValidateAndCopy rejects that exact sentinel outright, before
/// even reaching the Crc check, so a fresh ring reads as entirely absent by
/// construction rather than depending on a Crc-of-an-all-zero-body happening
/// to mismatch a zero-filled Crc field. If an existing file's length does not match
/// <c>Capacity * Stride</c> for the capacity requested this run (e.g. a
/// capacity constant changed between versions), the file is recreated from
/// scratch rather than reinterpreted - this store's data is a rolling cache
/// that is always safe to lose, never data requiring a migration.</para>
///
/// <para><b>Concurrency.</b> One writer at a time (the caller must serialize
/// its own WriteSlot calls - RingFile does not lock). Any number of readers
/// may call TryReadSlot/TryReadSlotAtIndex concurrently with the writer and
/// with each other, with no locking, per the write protocol above.</para>
/// </summary>
internal sealed unsafe class RingFile : IDisposable
{
    /// <summary>Sentinel LeadStamp/TrailStamp value meaning "this slot has
    /// never been written". No real epoch-second timestamp can equal
    /// <see cref="long.MinValue"/>, so this can never be confused with a
    /// legitimately stored second - including timestamp zero, which several
    /// callers use as an ordinary relative timestamp in tests.</summary>
    public const long UnwrittenStamp = long.MinValue;

    private const int LeadStampBytes = sizeof(long);
    private const int CrcBytes = sizeof(uint);
    private const int TrailStampBytes = sizeof(long);
    private const int EnvelopeBytes = LeadStampBytes + CrcBytes + TrailStampBytes;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _basePointer;
    private readonly int _leadOffset;
    private readonly int _bodyOffset;
    private readonly int _crcOffset;
    private readonly int _trailOffset;
    private bool _pointerAcquired;
    private bool _disposed;

    private long _pruneFloorSec;

    /// <summary>Number of slots in the ring.</summary>
    public long Capacity { get; }

    /// <summary>Size in bytes of the caller-defined body every slot carries,
    /// excluding RingFile's own stamp/Crc envelope.</summary>
    public int BodyLength { get; }

    /// <summary>Size in bytes of one whole slot (envelope + body).</summary>
    public int Stride { get; }

    /// <summary>Byte offset of LeadStamp within a slot (always 0). Exposed,
    /// with the three offsets below, only so tests can corrupt one specific
    /// field of a real on-disk slot to simulate a torn write, against this
    /// class's own layout instead of a second, independently-maintained
    /// copy of it.</summary>
    internal int LeadOffset => _leadOffset;

    /// <summary>Byte offset of the caller's body within a slot.</summary>
    internal int BodyOffset => _bodyOffset;

    /// <summary>Byte offset of the Crc field within a slot.</summary>
    internal int CrcOffset => _crcOffset;

    /// <summary>Byte offset of TrailStamp within a slot.</summary>
    internal int TrailOffset => _trailOffset;

    /// <summary>Timestamps below this floor read as absent regardless of
    /// what is physically stored - the caller-facing half of this store's
    /// retention/prune model (see BinaryMetricsHistoryStore). Raising this
    /// value never physically clears anything; it only hides slots that
    /// have not yet been overwritten by the ring's own wraparound. Reads and
    /// writes use <see cref="Volatile"/> so a reader on another thread sees
    /// an updated floor promptly and in order with everything the writer
    /// did before raising it.</summary>
    public long PruneFloorSec
    {
        get => Volatile.Read(ref _pruneFloorSec);
        set => Volatile.Write(ref _pruneFloorSec, value);
    }

    private RingFile(MemoryMappedFile file, MemoryMappedViewAccessor accessor, long capacity, int bodyLength, long initialPruneFloorSec)
    {
        _file = file;
        _accessor = accessor;
        Capacity = capacity;
        BodyLength = bodyLength;
        Stride = EnvelopeBytes + bodyLength;
        _pruneFloorSec = initialPruneFloorSec;

        _leadOffset = 0;
        _bodyOffset = LeadStampBytes;
        _crcOffset = _bodyOffset + bodyLength;
        _trailOffset = _crcOffset + CrcBytes;

        byte* pointer = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _pointerAcquired = true;
        _basePointer = pointer + accessor.PointerOffset;
    }

    /// <summary>Creates a new ring file, or opens an existing one whose size
    /// already matches <c>capacity * (bodyLength + envelope)</c>. Any other
    /// existing file at <paramref name="path"/> is discarded and reformatted
    /// - see the class doc's "Fresh vs reopened file" note.</summary>
    public static RingFile CreateOrOpen(string path, long capacity, int bodyLength, long initialPruneFloorSec = UnwrittenStamp)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "ring capacity must be positive.");
        }
        if (bodyLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLength), "ring body length must be positive.");
        }

        var stride = EnvelopeBytes + bodyLength;
        var expectedLength = checked(capacity * stride);

        var isFresh = true;
        using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            isFresh = fs.Length != expectedLength;
            if (isFresh)
            {
                fs.SetLength(expectedLength);
            }
        }

        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, expectedLength, MemoryMappedFileAccess.ReadWrite);
        var accessor = mmf.CreateViewAccessor(0, expectedLength, MemoryMappedFileAccess.ReadWrite);

        var ring = new RingFile(mmf, accessor, capacity, bodyLength, initialPruneFloorSec);
        if (isFresh)
        {
            ring.FormatAllSlots();
        }
        return ring;
    }

    private void FormatAllSlots()
    {
        for (long index = 0; index < Capacity; index++)
        {
            var slot = _basePointer + index * Stride;
            Unsafe.WriteUnaligned(slot + _leadOffset, UnwrittenStamp);
            Unsafe.WriteUnaligned(slot + _trailOffset, UnwrittenStamp);
        }
        Flush();
    }

    /// <summary>Writes one slot for <paramref name="ts"/>, overwriting
    /// whatever previously occupied <c>ts mod Capacity</c> - including a
    /// prior write for this same ts (a same-second replace) or, once the
    /// ring has wrapped, a different, older ts entirely. The caller owns
    /// serializing writes; RingFile does not lock.</summary>
    public void WriteSlot(long ts, ReadOnlySpan<byte> body)
    {
        if (body.Length != BodyLength)
        {
            throw new ArgumentException($"body must be exactly {BodyLength} bytes.", nameof(body));
        }

        var slot = SlotBase(ts);
        body.CopyTo(new Span<byte>(slot + _bodyOffset, BodyLength));
        var crc = Crc32.Compute(body);
        Unsafe.WriteUnaligned(slot + _crcOffset, crc);
        Unsafe.WriteUnaligned(slot + _trailOffset, ts);
        Volatile.Write(ref *(long*)(slot + _leadOffset), ts);
    }

    /// <summary>Reads the slot for <paramref name="ts"/> into
    /// <paramref name="body"/> (which must be exactly <see cref="BodyLength"/>
    /// bytes). Returns false - and leaves <paramref name="body"/> untouched
    /// - if that slot does not currently hold <paramref name="ts"/>, is
    /// below <see cref="PruneFloorSec"/>, or fails its Crc check.</summary>
    public bool TryReadSlot(long ts, Span<byte> body)
    {
        if (body.Length != BodyLength)
        {
            throw new ArgumentException($"body must be exactly {BodyLength} bytes.", nameof(body));
        }
        if (ts < PruneFloorSec)
        {
            return false;
        }

        return TryValidateAndCopy(SlotBase(ts), ts, body);
    }

    /// <summary>Reads whatever timestamp is currently stored at the raw slot
    /// <paramref name="index"/> (0 to Capacity-1), regardless of what that
    /// timestamp is - used to scan the whole ring once for a query window
    /// wide enough that iterating second-by-second would revisit the same
    /// physical slots many times over. Returns the stored timestamp via
    /// <paramref name="ts"/> even when the slot is rejected (below the
    /// prune floor or failing validation), so a caller can still see what
    /// physically occupies that index if it needs to.</summary>
    public bool TryReadSlotAtIndex(long index, Span<byte> body, out long ts)
    {
        if ((ulong)index >= (ulong)Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        if (body.Length != BodyLength)
        {
            throw new ArgumentException($"body must be exactly {BodyLength} bytes.", nameof(body));
        }

        var slot = _basePointer + index * Stride;
        var lead = Volatile.Read(ref *(long*)(slot + _leadOffset));
        ts = lead;
        if (lead < PruneFloorSec)
        {
            return false;
        }

        return TryValidateAndCopy(slot, lead, body);
    }

    private bool TryValidateAndCopy(byte* slot, long expectedTs, Span<byte> body)
    {
        // Rejected up front, not left to the Crc check below: a virgin slot
        // has LeadStamp == TrailStamp == UnwrittenStamp, and asking for
        // exactly that timestamp (TryReadSlotAtIndex passes the slot's own
        // stored lead value straight through as expectedTs, so this is the
        // normal case for every never-written slot a whole-ring scan visits)
        // must never be treated as a match regardless of what the Crc field
        // happens to hold.
        if (expectedTs == UnwrittenStamp)
        {
            return false;
        }

        var lead = Volatile.Read(ref *(long*)(slot + _leadOffset));
        if (lead != expectedTs)
        {
            return false;
        }

        var trail = Unsafe.ReadUnaligned<long>(slot + _trailOffset);
        if (trail != expectedTs)
        {
            return false;
        }

        var bodySpan = new ReadOnlySpan<byte>(slot + _bodyOffset, BodyLength);
        var storedCrc = Unsafe.ReadUnaligned<uint>(slot + _crcOffset);
        if (Crc32.Compute(bodySpan) != storedCrc)
        {
            return false;
        }

        bodySpan.CopyTo(body);
        return true;
    }

    private byte* SlotBase(long ts) => _basePointer + SlotIndex(ts) * Stride;

    private long SlotIndex(long ts) => ((ts % Capacity) + Capacity) % Capacity;

    /// <summary>Flushes the mapped view to disk (msync/FlushViewOfFile via
    /// the BCL). Cheap relative to the write volume this store expects
    /// (called once per MetricsHistory.FlushSeconds batch, not per
    /// second).</summary>
    public void Flush()
    {
        _accessor.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_pointerAcquired)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointerAcquired = false;
        }
        _accessor.Dispose();
        _file.Dispose();
    }
}
