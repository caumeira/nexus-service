using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Activity.Storage.Binary;

/// <summary>
/// Binary-file-backed IScreenTimeStore (Phase 7a of the metrics-store
/// design): one append-only day-segment file per LOCAL calendar date under
/// its own directory, plus a dedicated AppNameDictionary instance (opened
/// with StringComparer.Ordinal, not the metrics tier's case-insensitive one -
/// see that class's doc - since SqliteScreenTimeStore's sessions.app_name has
/// no COLLATE NOCASE and treats differently-cased names as distinct apps).
///
/// Sessions are one-record-per-focus-session (not one-record-per-tick like
/// AppUsageStore's ticks, which each carry several apps), so there is no
/// AppLocalIdMap per-day remap - the global AppNameDictionary id is small
/// enough (int32) to store directly, and screen-time volume (tens to
/// hundreds of sessions a day) never approaches the width AppLocalIdMap's
/// 16-bit local remap exists to bound. Each record is a fixed 25 bytes
/// (startUtcMs:i64, endUtcMs:i64, appNameId:i32, hourLocal:u8, crc32:u32),
/// day files are named by the session's own local start date
/// ("yyyy-MM-dd.seg") so the file set itself is the date index, and
/// TruncateTornTail runs before every append for the same reason
/// AppUsageStore's day segments need it: no long-lived writer handle exists
/// to catch a torn tail at open time, so a crash mid-append is instead caught
/// immediately before the next append lands.
///
/// QuerySessions is the one method taking an arbitrary UTC millisecond
/// window rather than local dates; it converts the window's upper bound to a
/// local date to skip day files that start strictly after it, but does not
/// bound the walk from below - a session's day file is named by its own
/// start date, and nothing here caps how long before the window that start
/// could be, so only the CRC/overlap filter, not the file selection, may
/// exclude a session on correctness grounds.
///
/// appPath is accepted (matching IScreenTimeStore's signature) but never
/// persisted - every real IScreenTimeProvider passes null for it today (see
/// WindowsScreenTimeProvider/MacScreenTimeProvider/LinuxScreenTimeProvider),
/// and the fixed 25-byte record has no field for it; QuerySessions always
/// returns AppPath null.
///
/// A single lock serializes every method, the same low-volume-store choice
/// PrivacyLog makes and for the same reason: RecordSession runs on a
/// platform provider's own thread while every read/delete method runs on a
/// Kestrel request thread, and this store's own file operations (a
/// concurrent append opening a day file mid-DeleteApp rewrite, or reading a
/// day file mid-append) are not safe to interleave without one.
/// </summary>
public sealed class BinaryScreenTimeStore : IScreenTimeStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private const int RecordBodyLength = 8 + 8 + 4 + 1;
    private const int RecordLength = RecordBodyLength + 4;

    private readonly string _dir;
    private readonly AppNameDictionary _names;
    private readonly object _lock = new();

    public BinaryScreenTimeStore(string dir)
    {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _names = AppNameDictionary.Open(Path.Combine(dir, "apps.dict"), StringComparer.Ordinal);
    }

    public void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return;
        }
        if (endedUtcMs <= startedUtcMs)
        {
            return;
        }

        lock (_lock)
        {
            var startedLocal = DateTimeOffset.FromUnixTimeMilliseconds(startedUtcMs).LocalDateTime;
            var dateLocal = startedLocal.ToString(DateFormat);
            var hourLocal = (byte)startedLocal.Hour;
            var appNameId = _names.RegisterOrGet(appName);

            AppendRecord(dateLocal, startedUtcMs, endedUtcMs, appNameId, hourLocal);
        }
    }

    public IReadOnlyList<FocusSessionRow> QuerySessions(long fromUtcMs, long toUtcMs)
    {
        lock (_lock)
        {
            var toLocalDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(toUtcMs).LocalDateTime).ToString(DateFormat);

            var result = new List<FocusSessionRow>();
            foreach (var dateLocal in ExistingDates().Where(d => string.CompareOrdinal(d, toLocalDate) <= 0))
            {
                foreach (var row in ReadDay(dateLocal))
                {
                    if (row.EndUtcMs >= fromUtcMs && row.StartUtcMs <= toUtcMs)
                    {
                        result.Add(new FocusSessionRow(_names.GetName(row.AppNameId), null, row.StartUtcMs, row.EndUtcMs));
                    }
                }
            }
            result.Sort((a, b) => a.StartedUtcMs.CompareTo(b.StartedUtcMs));
            return result;
        }
    }

    public DayBreakdown GetDay(DateOnly localDate)
    {
        lock (_lock)
        {
            var dateStr = localDate.ToString(DateFormat);
            var result = new DayBreakdown { Date = dateStr, HourlyMs = new List<long>(new long[24]) };

            var totalByApp = new Dictionary<int, long>();
            foreach (var row in ReadDay(dateStr))
            {
                var duration = row.EndUtcMs - row.StartUtcMs;
                result.TotalMs += duration;
                result.Pickups++;
                totalByApp[row.AppNameId] = totalByApp.GetValueOrDefault(row.AppNameId) + duration;
                if (row.HourLocal < 24)
                {
                    result.HourlyMs[row.HourLocal] += duration;
                }
            }

            result.Apps = totalByApp
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new AppUsage { Name = _names.GetName(kv.Key), TotalMs = kv.Value })
                .ToList();
            return result;
        }
    }

    public IReadOnlyList<DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_lock)
        {
            var result = new List<DayTotal>();
            foreach (var dateLocal in DatesBetween(fromInclusive.ToString(DateFormat), toInclusive.ToString(DateFormat)))
            {
                var rows = ReadDay(dateLocal);
                if (rows.Count == 0)
                {
                    continue;
                }
                result.Add(new DayTotal
                {
                    Date = dateLocal,
                    TotalMs = rows.Sum(r => r.EndUtcMs - r.StartUtcMs),
                    Pickups = rows.Count,
                });
            }
            return result;
        }
    }

    public AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_lock)
        {
            var result = new AppHistory { AppName = appName };
            if (_names.TryGetId(appName) is not { } appId)
            {
                return result;
            }

            long longest = 0;
            foreach (var dateLocal in DatesBetween(fromInclusive.ToString(DateFormat), toInclusive.ToString(DateFormat)))
            {
                long dayTotal = 0;
                var dayPickups = 0;
                foreach (var row in ReadDay(dateLocal))
                {
                    if (row.AppNameId != appId)
                    {
                        continue;
                    }
                    var duration = row.EndUtcMs - row.StartUtcMs;
                    dayTotal += duration;
                    dayPickups++;
                    longest = Math.Max(longest, duration);
                }
                if (dayPickups == 0)
                {
                    continue;
                }
                result.Daily.Add(new DayTotal { Date = dateLocal, TotalMs = dayTotal, Pickups = dayPickups });
                result.TotalMs += dayTotal;
                result.TotalPickups += dayPickups;
            }
            result.LongestSessionMs = longest;
            return result;
        }
    }

    public IReadOnlyList<AppUsage> GetTodayUsage(DateOnly today) => GetDay(today).Apps;

    public IReadOnlyList<AppUsage> GetHourUsage(DateOnly localDate, int hourLocal)
    {
        if (hourLocal < 0 || hourLocal > 23)
        {
            return Array.Empty<AppUsage>();
        }

        lock (_lock)
        {
            var totalByApp = new Dictionary<int, long>();
            foreach (var row in ReadDay(localDate.ToString(DateFormat)))
            {
                if (row.HourLocal != hourLocal)
                {
                    continue;
                }
                totalByApp[row.AppNameId] = totalByApp.GetValueOrDefault(row.AppNameId) + (row.EndUtcMs - row.StartUtcMs);
            }

            return totalByApp
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new AppUsage { Name = _names.GetName(kv.Key), TotalMs = kv.Value })
                .ToList();
        }
    }

    public int DeleteDay(DateOnly localDate)
    {
        lock (_lock)
        {
            var dateStr = localDate.ToString(DateFormat);
            var count = ReadDay(dateStr).Count;
            if (count > 0)
            {
                TryDelete(SegPath(dateStr));
            }
            return count;
        }
    }

    public int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_lock)
        {
            var deleted = 0;
            foreach (var dateLocal in DatesBetween(fromInclusive.ToString(DateFormat), toInclusive.ToString(DateFormat)))
            {
                var count = ReadDay(dateLocal).Count;
                if (count == 0)
                {
                    continue;
                }
                TryDelete(SegPath(dateLocal));
                deleted += count;
            }
            return deleted;
        }
    }

    public int DeleteApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return 0;
        }

        lock (_lock)
        {
            if (_names.TryGetId(appName) is not { } appId)
            {
                return 0;
            }

            var deleted = 0;
            foreach (var dateLocal in ExistingDates())
            {
                var rows = ReadDay(dateLocal);
                var survivors = rows.Where(r => r.AppNameId != appId).ToList();
                var removed = rows.Count - survivors.Count;
                if (removed == 0)
                {
                    continue;
                }
                deleted += removed;
                RewriteDay(dateLocal, survivors);
            }
            return deleted;
        }
    }

    public int DeleteAll()
    {
        lock (_lock)
        {
            var deleted = 0;
            foreach (var dateLocal in ExistingDates())
            {
                deleted += ReadDay(dateLocal).Count;
                TryDelete(SegPath(dateLocal));
            }
            return deleted;
        }
    }

    public void Dispose() => _names.Dispose();

    private string SegPath(string dateLocal) => Path.Combine(_dir, $"{dateLocal}.seg");

    private List<string> ExistingDates() =>
        Directory.GetFiles(_dir, "*.seg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

    private IEnumerable<string> DatesBetween(string fromInclusive, string toInclusive) =>
        ExistingDates().Where(d => string.CompareOrdinal(d, fromInclusive) >= 0 && string.CompareOrdinal(d, toInclusive) <= 0);

    private readonly record struct SessionRecord(long StartUtcMs, long EndUtcMs, int AppNameId, byte HourLocal);

    private List<SessionRecord> ReadDay(string dateLocal)
    {
        var path = SegPath(dateLocal);
        if (!File.Exists(path))
        {
            return new List<SessionRecord>();
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return new List<SessionRecord>();
        }

        var result = new List<SessionRecord>(bytes.Length / RecordLength);
        var pos = 0;
        while (pos + RecordLength <= bytes.Length)
        {
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, RecordBodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + RecordBodyLength, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }

            var start = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos, 8));
            var end = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos + 8, 8));
            var appNameId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 16, 4));
            var hourLocal = bytes[pos + 20];
            result.Add(new SessionRecord(start, end, appNameId, hourLocal));
            pos += RecordLength;
        }
        return result;
    }

    private void AppendRecord(string dateLocal, long startUtcMs, long endUtcMs, int appNameId, byte hourLocal)
    {
        var path = SegPath(dateLocal);
        TruncateTornTail(path);
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        WriteRecord(fs, startUtcMs, endUtcMs, appNameId, hourLocal);
        fs.Flush(flushToDisk: true);
    }

    private static void WriteRecord(FileStream fs, long startUtcMs, long endUtcMs, int appNameId, byte hourLocal)
    {
        Span<byte> buf = stackalloc byte[RecordLength];
        BinaryPrimitives.WriteInt64LittleEndian(buf[..8], startUtcMs);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(8, 8), endUtcMs);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(16, 4), appNameId);
        buf[20] = hourLocal;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(RecordBodyLength, 4), Crc32.Compute(buf[..RecordBodyLength]));
        fs.Write(buf);
    }

    // A crash mid-append can leave a torn trailing record; there is no
    // long-lived writer handle to catch it at load time (unlike
    // AppNameDictionary/EntityRegistry), so this runs immediately before
    // every append instead - see AppUsageStore.TruncateTornTail, the same
    // fix for the same structural gap.
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

    private void RewriteDay(string dateLocal, List<SessionRecord> survivors)
    {
        var path = SegPath(dateLocal);
        if (survivors.Count == 0)
        {
            TryDelete(path);
            return;
        }

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var r in survivors)
            {
                WriteRecord(fs, r.StartUtcMs, r.EndUtcMs, r.AppNameId, r.HourLocal);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
