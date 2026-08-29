using System.Buffers.Binary;
using System.Text;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Games;

public enum FpsUploadState : byte { Pending = 0, Sent = 1, Rejected = 2 }

/// <summary>One completed, persisted game session summary - the fixed-size
/// record BinaryFpsSessionStore reads and writes. Hist must be exactly
/// FpsHistogram.BucketCount long. Frames is the sum of ValidSec once-per-
/// second fps samples (Frames/ValidSec is the session's average fps), not a
/// count of individual presents.</summary>
public sealed record FpsSessionRecord(
    Guid Id, string GameKey, string GameName, string Store,
    long StartedUtcMs, long EndedUtcMs, int FocusedSec, int ValidSec, long Frames,
    int MinFps, int MaxFps, uint[] Hist,
    int DispW, int DispH, int RefreshHz, int WinW, int WinH,
    bool Fullscreen, bool Capped, int CapValue, ulong HardwareHash, FpsUploadState UploadState);

/// <summary>Per-game rollup across every stored session for that gameKey,
/// used by GET /api/fps/games. Hist is the bucket-wise merge of every
/// session's hist, so p1/p99 are derived here rather than re-walking raw
/// sessions.</summary>
public sealed record FpsGameSummary(
    string GameKey, string GameName, string Store, string? SteamAppId,
    int Sessions, long FocusedSec, long ValidSec, long Frames,
    int MinFps, int MaxFps, uint[] Hist, long LastPlayedUtcMs);

/// <summary>
/// Binary-file-backed per-session fps store under db/fps/: fixed-size CRC'd
/// records in per-UTC-month segment files ("yyyy-MM.seg"), plus a
/// GameDictionary mapping gameKey (kept stable across a game's lifetime) to
/// an int id, the same shape BinaryScreenTimeStore uses for its day
/// segments and AppNameDictionary. One year retention: a month segment is
/// dropped once every record in it is older than the retention window,
/// checked on an internal hourly throttle inside Append rather than a
/// dedicated worker, since sessions close infrequently enough that this is
/// no less prompt.
///
/// A separate store from screen time and the /monitoring/history fps
/// series: DELETE /api/fps/all purges only this store's segments, leaving
/// the other two untouched.
/// </summary>
public sealed class BinaryFpsSessionStore : IDisposable
{
    private const string DateFormat = "yyyy-MM";
    private const int HistBytes = sizeof(uint) * FpsHistogram.BucketCount;

    // id(16) gameId(4) startedUtcMs(8) endedUtcMs(8) focusedSec(4) validSec(4)
    // frames(8) minFps(2) maxFps(2) hist(256) dispW(2) dispH(2) refreshHz(2)
    // winW(2) winH(2) flags(1) capValue(2) hardwareHash(8) uploadState(1)
    private const int RecordBodyLength =
        16 + 4 + 8 + 8 + 4 + 4 + 8 + 2 + 2 + HistBytes + 2 + 2 + 2 + 2 + 2 + 1 + 2 + 8 + 1;
    private const int RecordLength = RecordBodyLength + 4; // + crc32

    private const byte FlagFullscreen = 0x01;
    private const byte FlagCapped = 0x02;

    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);
    private static readonly TimeSpan PruneCheckInterval = TimeSpan.FromHours(1);

    private readonly string _dir;
    private readonly GameDictionary _games;
    private readonly object _lock = new();
    private DateTime _lastPruneCheckUtc = DateTime.MinValue;

    public BinaryFpsSessionStore(string dir)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _games = GameDictionary.Open(Path.Combine(dir, "games.dict"));
    }

    public void Append(FpsSessionRecord record)
    {
        if (record.Hist.Length != FpsHistogram.BucketCount)
        {
            throw new ArgumentException($"hist must be exactly {FpsHistogram.BucketCount} buckets.", nameof(record));
        }

        lock (_lock)
        {
            var gameId = _games.RegisterOrGet(record.GameKey, record.GameName, record.Store);
            var monthUtc = DateTimeOffset.FromUnixTimeMilliseconds(record.StartedUtcMs).UtcDateTime.ToString(DateFormat);
            AppendRecord(monthUtc, record, gameId);
            MaybePrune();
        }
    }

    public IReadOnlyList<FpsSessionRecord> QuerySessions(string gameKey, int limit)
    {
        lock (_lock)
        {
            if (_games.TryGetId(gameKey) is not { } gameId)
            {
                return Array.Empty<FpsSessionRecord>();
            }

            var result = new List<FpsSessionRecord>();
            foreach (var month in ExistingMonths().OrderByDescending(m => m, StringComparer.Ordinal))
            {
                foreach (var record in ReadMonth(month).Where(r => r.GameId == gameId).OrderByDescending(r => r.StartedUtcMs))
                {
                    result.Add(ToRecord(record));
                    if (result.Count >= limit)
                    {
                        return result;
                    }
                }
            }
            return result;
        }
    }

    /// <summary>Every session (across every game) whose [StartedUtcMs,
    /// EndedUtcMs] overlaps [fromUtcMs, toUtcMs], newest first, capped at
    /// limit. Scans only the month segments that can possibly hold such a
    /// session rather than every existing month.</summary>
    public IReadOnlyList<FpsSessionRecord> QuerySessionsByTimeRange(long fromUtcMs, long toUtcMs, int limit)
    {
        lock (_lock)
        {
            var result = new List<FpsSessionRecord>();
            foreach (var month in MonthsPossiblyOverlapping(fromUtcMs, toUtcMs).OrderByDescending(m => m, StringComparer.Ordinal))
            {
                var matches = ReadMonth(month)
                    .Where(r => r.EndedUtcMs >= fromUtcMs && r.StartedUtcMs <= toUtcMs)
                    .OrderByDescending(r => r.StartedUtcMs);
                foreach (var raw in matches)
                {
                    result.Add(ToRecord(raw));
                    if (result.Count >= limit)
                    {
                        return result;
                    }
                }
            }
            return result;
        }
    }

    // A session's segment file is keyed by its own start month, but its end
    // can spill one month past that for a session that started near a
    // month boundary; pads the scan by one month before fromUtcMs to catch
    // those without scanning every month on disk.
    private IEnumerable<string> MonthsPossiblyOverlapping(long fromUtcMs, long toUtcMs)
    {
        var existing = new HashSet<string>(ExistingMonths(), StringComparer.Ordinal);
        var toMonthKey = DateTimeOffset.FromUnixTimeMilliseconds(toUtcMs).UtcDateTime.ToString(DateFormat);

        var fromMonthStart = DateTimeOffset.FromUnixTimeMilliseconds(fromUtcMs).UtcDateTime;
        var cursor = new DateTime(fromMonthStart.Year, fromMonthStart.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);

        // Bounds a caller passing an inverted or pathologically wide range;
        // the route validates to >= from, but this method is public.
        for (var iterations = 0; iterations < 2400; iterations++)
        {
            var key = cursor.ToString(DateFormat);
            if (existing.Contains(key))
            {
                yield return key;
            }
            if (string.CompareOrdinal(key, toMonthKey) >= 0)
            {
                yield break;
            }
            cursor = cursor.AddMonths(1);
        }
    }

    public IReadOnlyList<FpsGameSummary> QueryGameSummaries()
    {
        lock (_lock)
        {
            var byGame = new Dictionary<int, (int Sessions, long FocusedSec, long ValidSec, long Frames, int MinFps, int MaxFps, uint[] Hist, long LastPlayedUtcMs)>();
            foreach (var month in ExistingMonths())
            {
                foreach (var raw in ReadMonth(month))
                {
                    if (byGame.TryGetValue(raw.GameId, out var acc))
                    {
                        byGame[raw.GameId] = (
                            acc.Sessions + 1,
                            acc.FocusedSec + raw.FocusedSec,
                            acc.ValidSec + raw.ValidSec,
                            acc.Frames + raw.Frames,
                            Math.Min(acc.MinFps, raw.MinFps),
                            Math.Max(acc.MaxFps, raw.MaxFps),
                            FpsHistogram.Merge(acc.Hist, raw.Hist),
                            Math.Max(acc.LastPlayedUtcMs, raw.EndedUtcMs));
                    }
                    else
                    {
                        byGame[raw.GameId] = (1, raw.FocusedSec, raw.ValidSec, raw.Frames, raw.MinFps, raw.MaxFps, raw.Hist, raw.EndedUtcMs);
                    }
                }
            }

            var result = new List<FpsGameSummary>(byGame.Count);
            foreach (var (gameId, acc) in byGame)
            {
                var (gameKey, name, store) = _games.GetEntry(gameId);
                var steamAppId = store == "steam" && gameKey.StartsWith("steam:", StringComparison.Ordinal)
                    ? gameKey["steam:".Length..]
                    : null;
                result.Add(new FpsGameSummary(
                    gameKey, name, store, steamAppId,
                    acc.Sessions, acc.FocusedSec, acc.ValidSec, acc.Frames, acc.MinFps, acc.MaxFps, acc.Hist, acc.LastPlayedUtcMs));
            }
            return result;
        }
    }

    /// <summary>Every session with UploadState Pending, oldest first, capped
    /// at max - the FIFO order lets a large backlog drain across successive
    /// hourly batches instead of the newest sessions perpetually crowding
    /// out the oldest.</summary>
    public IReadOnlyList<FpsSessionRecord> QueryPendingForUpload(int max)
    {
        lock (_lock)
        {
            var result = new List<FpsSessionRecord>();
            foreach (var month in ExistingMonths().OrderBy(m => m, StringComparer.Ordinal))
            {
                foreach (var raw in ReadMonth(month).Where(r => r.UploadState == FpsUploadState.Pending).OrderBy(r => r.StartedUtcMs))
                {
                    result.Add(ToRecord(raw));
                    if (result.Count >= max)
                    {
                        return result;
                    }
                }
            }
            return result;
        }
    }

    /// <summary>Rewrites the UploadState of every session in ids, across
    /// whichever month segments hold them. A no-op id (already gone, or
    /// never existed) is silently ignored.</summary>
    public void MarkUploadState(IEnumerable<Guid> ids, FpsUploadState state)
    {
        var idSet = new HashSet<Guid>(ids);
        if (idSet.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var month in ExistingMonths())
            {
                var records = ReadMonth(month);
                var changed = false;
                for (var i = 0; i < records.Count; i++)
                {
                    if (idSet.Contains(records[i].Id))
                    {
                        records[i] = records[i] with { UploadState = state };
                        changed = true;
                    }
                }
                if (changed)
                {
                    RewriteMonth(month, records);
                }
            }
        }
    }

    public int DeleteAll()
    {
        lock (_lock)
        {
            var deleted = 0;
            foreach (var month in ExistingMonths())
            {
                deleted += ReadMonth(month).Count;
                TryDelete(SegPath(month));
            }
            return deleted;
        }
    }

    /// <summary>Deletes exactly one session by id. Returns 1 if found and
    /// removed, 0 if no session with that id exists in any month
    /// segment.</summary>
    public int DeleteSession(Guid id)
    {
        lock (_lock)
        {
            foreach (var month in ExistingMonths())
            {
                var records = ReadMonth(month);
                var index = records.FindIndex(r => r.Id == id);
                if (index < 0)
                {
                    continue;
                }
                records.RemoveAt(index);
                RewriteMonth(month, records);
                return 1;
            }
            return 0;
        }
    }

    /// <summary>Deletes every session for gameKey, across every month
    /// segment that holds one. Returns the number of sessions removed; 0
    /// for an unknown gameKey. The GameDictionary entry itself is left in
    /// place - it is cheap metadata, and no segment will reference it once
    /// this returns.</summary>
    public int DeleteGame(string gameKey)
    {
        lock (_lock)
        {
            if (_games.TryGetId(gameKey) is not { } gameId)
            {
                return 0;
            }

            var removed = 0;
            foreach (var month in ExistingMonths())
            {
                var records = ReadMonth(month);
                var survivors = records.Where(r => r.GameId != gameId).ToList();
                var removedThisMonth = records.Count - survivors.Count;
                if (removedThisMonth == 0)
                {
                    continue;
                }
                removed += removedThisMonth;
                RewriteMonth(month, survivors);
            }
            return removed;
        }
    }

    /// <summary>Deletes every session whose StartedUtcMs, converted to the
    /// service's local calendar date, falls in [from, to] inclusive. Returns
    /// the number of sessions removed.</summary>
    public int DeleteRange(DateOnly from, DateOnly to)
    {
        lock (_lock)
        {
            var deleted = 0;
            foreach (var month in MonthsPossiblyOverlappingLocalRange(from, to))
            {
                var records = ReadMonth(month);
                var survivors = records.Where(r => !IsInLocalDateRange(r.StartedUtcMs, from, to)).ToList();
                var removedThisMonth = records.Count - survivors.Count;
                if (removedThisMonth == 0)
                {
                    continue;
                }
                deleted += removedThisMonth;
                RewriteMonth(month, survivors);
            }
            return deleted;
        }
    }

    private static bool IsInLocalDateRange(long startedUtcMs, DateOnly from, DateOnly to)
    {
        var localDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(startedUtcMs).ToLocalTime().Date);
        return localDate >= from && localDate <= to;
    }

    // A session's UTC month segment can differ from its local calendar
    // date's month by one in either direction near a month boundary, so this
    // pads one month on each side of [from, to] rather than computing the
    // exact UTC offset.
    private IEnumerable<string> MonthsPossiblyOverlappingLocalRange(DateOnly from, DateOnly to)
    {
        var existing = new HashSet<string>(ExistingMonths(), StringComparer.Ordinal);

        var fromUtc = new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var toUtc = new DateTime(to.Year, to.Month, to.Day, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();

        var cursor = ClampedMonthStart(fromUtc.Year, fromUtc.Month, -1);
        var toMonthKeyPadded = ClampedMonthStart(toUtc.Year, toUtc.Month, 1).ToString(DateFormat);

        // Bounds an inverted or pathologically wide range; the route
        // validates to >= from, but DeleteRange must stay safe on its own.
        for (var iterations = 0; iterations < 2400; iterations++)
        {
            var key = cursor.ToString(DateFormat);
            if (existing.Contains(key))
            {
                yield return key;
            }
            if (string.CompareOrdinal(key, toMonthKeyPadded) >= 0)
            {
                yield break;
            }
            cursor = cursor.AddMonths(1);
        }
    }

    // DateTime.AddMonths throws past year 1 or year 9999; a DateOnly at
    // either edge is reachable through the public route, so this clamps to
    // the nearest valid month instead.
    private static DateTime ClampedMonthStart(int year, int month, int monthOffset)
    {
        const int MaxMonthIndex = (9999 - 1) * 12 + 11;
        var index = Math.Clamp((year - 1) * 12 + (month - 1) + monthOffset, 0, MaxMonthIndex);
        return new DateTime(index / 12 + 1, index % 12 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    // Rewrites a month segment from scratch with exactly survivors (deletes
    // the file outright if that leaves nothing) - the same temp-file-then-move
    // shape BinaryScreenTimeStore.RewriteDay uses for the same reason: a
    // crash mid-write leaves the original file intact rather than a
    // partially-rewritten one.
    private void RewriteMonth(string month, List<RawRecord> survivors)
    {
        var path = SegPath(month);
        if (survivors.Count == 0)
        {
            TryDelete(path);
            return;
        }

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Span<byte> buf = stackalloc byte[RecordLength];
            foreach (var r in survivors)
            {
                EncodeRaw(r, buf[..RecordBodyLength]);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(RecordBodyLength, 4), Crc32.Compute(buf[..RecordBodyLength]));
                fs.Write(buf);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() => _games.Dispose();

    private void MaybePrune()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPruneCheckUtc < PruneCheckInterval)
        {
            return;
        }
        _lastPruneCheckUtc = now;

        var cutoff = now - RetentionWindow;
        foreach (var month in ExistingMonths())
        {
            // A month is dropped once it is wholly outside the retention
            // window - compare against the month's own LAST possible day
            // (start of next month minus a tick) so a segment still
            // partially within the window is never dropped early.
            if (!DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var monthStart))
            {
                continue;
            }
            var monthEndExclusive = monthStart.AddMonths(1);
            if (monthEndExclusive <= cutoff)
            {
                TryDelete(SegPath(month));
            }
        }
    }

    private string SegPath(string month) => Path.Combine(_dir, $"{month}.seg");

    private List<string> ExistingMonths() =>
        Directory.GetFiles(_dir, "*.seg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .ToList();

    private readonly record struct RawRecord(
        Guid Id, int GameId, long StartedUtcMs, long EndedUtcMs, int FocusedSec, int ValidSec, long Frames,
        int MinFps, int MaxFps, uint[] Hist, int DispW, int DispH, int RefreshHz, int WinW, int WinH,
        byte Flags, int CapValue, ulong HardwareHash, FpsUploadState UploadState);

    private FpsSessionRecord ToRecord(RawRecord r)
    {
        var (gameKey, name, store) = _games.GetEntry(r.GameId);
        return new FpsSessionRecord(
            r.Id, gameKey, name, store, r.StartedUtcMs, r.EndedUtcMs, r.FocusedSec, r.ValidSec, r.Frames,
            r.MinFps, r.MaxFps, r.Hist, r.DispW, r.DispH, r.RefreshHz, r.WinW, r.WinH,
            (r.Flags & FlagFullscreen) != 0, (r.Flags & FlagCapped) != 0, r.CapValue, r.HardwareHash, r.UploadState);
    }

    private List<RawRecord> ReadMonth(string month)
    {
        var path = SegPath(month);
        if (!File.Exists(path))
        {
            return new List<RawRecord>();
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return new List<RawRecord>();
        }

        var result = new List<RawRecord>(bytes.Length / RecordLength);
        var pos = 0;
        while (pos + RecordLength <= bytes.Length)
        {
            var body = bytes.AsSpan(pos, RecordBodyLength);
            var expectedCrc = Crc32.Compute(body);
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + RecordBodyLength, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            result.Add(Decode(body));
            pos += RecordLength;
        }
        return result;
    }

    private void AppendRecord(string month, FpsSessionRecord record, int gameId)
    {
        var path = SegPath(month);
        TruncateTornTail(path);
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);

        Span<byte> buf = stackalloc byte[RecordLength];
        Encode(record, gameId, buf[..RecordBodyLength]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(RecordBodyLength, 4), Crc32.Compute(buf[..RecordBodyLength]));
        fs.Write(buf);
        fs.Flush(flushToDisk: true);
    }

    private static void Encode(FpsSessionRecord r, int gameId, Span<byte> body)
    {
        var offset = 0;
        r.Id.TryWriteBytes(body.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), gameId);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.StartedUtcMs);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.EndedUtcMs);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), r.FocusedSec);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), r.ValidSec);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.Frames);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.MinFps, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.MaxFps, 0, ushort.MaxValue));
        offset += 2;
        for (var i = 0; i < FpsHistogram.BucketCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(offset, 4), r.Hist[i]);
            offset += 4;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.DispW, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.DispH, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.RefreshHz, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.WinW, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.WinH, 0, ushort.MaxValue));
        offset += 2;
        byte flags = 0;
        if (r.Fullscreen) flags |= FlagFullscreen;
        if (r.Capped) flags |= FlagCapped;
        body[offset] = flags;
        offset += 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.CapValue, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(offset, 8), r.HardwareHash);
        offset += 8;
        body[offset] = (byte)r.UploadState;
    }

    // Same field layout as Encode, sourced from an already-decoded RawRecord
    // (GameId and Flags are already the on-disk int/byte, not the
    // FpsSessionRecord/gameId pair Encode packs) - used to rewrite a month
    // segment's surviving records after a delete.
    private static void EncodeRaw(RawRecord r, Span<byte> body)
    {
        var offset = 0;
        r.Id.TryWriteBytes(body.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), r.GameId);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.StartedUtcMs);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.EndedUtcMs);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), r.FocusedSec);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(offset, 4), r.ValidSec);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(offset, 8), r.Frames);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.MinFps, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.MaxFps, 0, ushort.MaxValue));
        offset += 2;
        for (var i = 0; i < FpsHistogram.BucketCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(offset, 4), r.Hist[i]);
            offset += 4;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.DispW, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.DispH, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.RefreshHz, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.WinW, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.WinH, 0, ushort.MaxValue));
        offset += 2;
        body[offset] = r.Flags;
        offset += 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(offset, 2), (ushort)Math.Clamp(r.CapValue, 0, ushort.MaxValue));
        offset += 2;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(offset, 8), r.HardwareHash);
        offset += 8;
        body[offset] = (byte)r.UploadState;
    }

    private static RawRecord Decode(ReadOnlySpan<byte> body)
    {
        var offset = 0;
        var id = new Guid(body.Slice(offset, 16));
        offset += 16;
        var gameId = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        var startedUtcMs = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(offset, 8));
        offset += 8;
        var endedUtcMs = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(offset, 8));
        offset += 8;
        var focusedSec = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        var validSec = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        var frames = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(offset, 8));
        offset += 8;
        var minFps = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var maxFps = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var hist = new uint[FpsHistogram.BucketCount];
        for (var i = 0; i < FpsHistogram.BucketCount; i++)
        {
            hist[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset, 4));
            offset += 4;
        }
        var dispW = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var dispH = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var refreshHz = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var winW = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var winH = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var flags = body[offset];
        offset += 1;
        var capValue = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        var hardwareHash = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(offset, 8));
        offset += 8;
        var uploadState = (FpsUploadState)body[offset];

        return new RawRecord(
            id, gameId, startedUtcMs, endedUtcMs, focusedSec, validSec, frames, minFps, maxFps, hist,
            dispW, dispH, refreshHz, winW, winH, flags, capValue, hardwareHash, uploadState);
    }

    // A crash mid-append can leave a torn trailing record; there is no
    // long-lived writer handle to catch it at load time, so this runs
    // immediately before every append instead (matching BinaryScreenTimeStore's
    // TruncateTornTail).
    private static void TruncateTornTail(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        while (pos + RecordLength <= bytes.Length)
        {
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, RecordBodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + RecordBodyLength, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            pos += RecordLength;
        }

        if (pos != bytes.Length)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(pos);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}

/// <summary>Append-only gameKey|name|store -&gt; id dictionary, the
/// per-store-fields sibling of Monitoring.History.Binary.AppNameDictionary
/// (append-order-is-index, no capacity cap - a box accumulates at most a
/// few hundred distinct games). Keyed by gameKey; name/store ride along so a
/// session record only needs to store the small int id.</summary>
internal sealed class GameDictionary : IDisposable
{
    private readonly FileStream _file;
    private readonly List<(string GameKey, string Name, string Store)> _entries = new();
    private readonly Dictionary<string, int> _idByGameKey = new(StringComparer.Ordinal);

    private GameDictionary(FileStream file)
    {
        _file = file;
        Load();
    }

    public static GameDictionary Open(string path) =>
        new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read));

    public int RegisterOrGet(string gameKey, string name, string store)
    {
        if (_idByGameKey.TryGetValue(gameKey, out var existing))
        {
            return existing;
        }

        var id = _entries.Count;
        WriteRecord(gameKey, name, store);
        _entries.Add((gameKey, name, store));
        _idByGameKey[gameKey] = id;
        return id;
    }

    public int? TryGetId(string gameKey) => _idByGameKey.TryGetValue(gameKey, out var id) ? id : null;

    public (string GameKey, string Name, string Store) GetEntry(int id) => _entries[id];

    private void WriteRecord(string gameKey, string name, string store)
    {
        _file.Seek(0, SeekOrigin.End);
        WriteLengthPrefixed(gameKey);
        WriteLengthPrefixed(name);
        WriteLengthPrefixed(store);
        _file.Flush(flushToDisk: true);
    }

    private void WriteLengthPrefixed(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        _file.Write(header);
        _file.Write(bytes);
    }

    private void Load()
    {
        _file.Seek(0, SeekOrigin.Begin);
        while (true)
        {
            var recordStart = _file.Position;
            if (!TryReadLengthPrefixed(out var gameKey) ||
                !TryReadLengthPrefixed(out var name) ||
                !TryReadLengthPrefixed(out var store))
            {
                _file.SetLength(recordStart);
                break;
            }
            _idByGameKey[gameKey] = _entries.Count;
            _entries.Add((gameKey, name, store));
        }
        _file.Seek(0, SeekOrigin.End);
    }

    private bool TryReadLengthPrefixed(out string value)
    {
        value = "";
        Span<byte> header = stackalloc byte[4];
        if (_file.Read(header) != 4)
        {
            return false;
        }
        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0)
        {
            return false;
        }
        var bytes = new byte[len];
        if (_file.Read(bytes) != len)
        {
            return false;
        }
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    public void Dispose() => _file.Dispose();
}
