using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Monitoring.Events.Binary;

/// <summary>
/// Binary-file-backed IMonitoringEventStore: a single append-only log file
/// plus an in-RAM list, modeled on PrivacyLog (see that class's doc) but for
/// discrete point events rather than sessions - every Append is a brand new
/// record, never an update-in-place, so a delete (custom events only) or a
/// prune must actually drop rows from the file rather than layer a
/// superseding record on top.
///
/// Ids are assigned from an in-RAM counter resumed from the highest id seen
/// on replay (LoadFromLog), so they stay monotonic and unique across
/// restarts without a separate counter file. Events are low-volume (usb
/// attach/detach, app opens, permission requests, occasional custom labels),
/// the same call-volume assumption PrivacyLog documents for its own single
/// plain lock instead of RingFile's lock-free discipline.
///
/// DeleteCustom and PruneOlderThan both rewrite the whole log from the
/// surviving in-RAM events (write to a temp file, flush, then atomic-rename
/// over the original), the same crash-safety shape as PrivacyLog.RewriteCompacted.
/// </summary>
internal sealed class MonitoringEventLog
{
    // Detail is the one nullable string field; a length prefix of -1 marks
    // "no detail" on disk. Kind/Label are never null, so any negative length
    // prefix for them is corruption. -2 and below is corruption for Detail
    // too - only exactly -1 is the null sentinel.
    private const int NullDetailLength = -1;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<MonitoringEvent> _events = new();
    private long _nextId = 1;

    public MonitoringEventLog(string path)
    {
        _path = path;
        if (!File.Exists(_path))
        {
            using var created = File.Create(_path);
        }
        LoadFromLog();
    }

    public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom)
    {
        lock (_lock)
        {
            var id = _nextId;
            var ev = new MonitoringEvent(id, tUtcMs, kind, label, detail, custom);
            // Disk before RAM: if AppendRecord throws, _events must not
            // already reflect a state a crash would then lose, and _nextId
            // stays unadvanced so the next attempt retries the same id -
            // same ordering PrivacyLog.Upsert documents for its own append.
            AppendRecord(ev);
            _nextId++;
            _events.Add(ev);
            return ev;
        }
    }

    public IReadOnlyList<MonitoringEvent> Query(long fromUtcMs, long toUtcMs, int limit)
    {
        lock (_lock)
        {
            var matches = _events
                .Where(e => e.TUtcMs >= fromUtcMs && e.TUtcMs <= toUtcMs)
                .OrderBy(e => e.TUtcMs)
                .ToList();
            return matches.Count <= limit ? matches : matches.Skip(matches.Count - limit).ToList();
        }
    }

    public bool DeleteCustom(long id)
    {
        lock (_lock)
        {
            var index = _events.FindIndex(e => e.Id == id);
            if (index < 0 || !_events[index].Custom)
            {
                return false;
            }
            _events.RemoveAt(index);
            RewriteCompacted();
            return true;
        }
    }

    public void PruneOlderThan(long cutoffUtcMs)
    {
        lock (_lock)
        {
            var removed = _events.RemoveAll(e => e.TUtcMs < cutoffUtcMs);
            if (removed > 0)
            {
                RewriteCompacted();
            }
        }
    }

    // Replays every whole, CRC-valid record in file order, tracking the
    // highest id seen so _nextId resumes past it. Stops at the first record
    // that fails a bounds or CRC check and truncates the file to that clean
    // boundary, the same recovery shape as PrivacyLog.LoadFromLog.
    private void LoadFromLog()
    {
        var bytes = File.ReadAllBytes(_path);
        var pos = 0;
        var maxId = 0L;
        while (pos + 16 <= bytes.Length)
        {
            var cursor = pos;
            var id = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            var tUtcMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;

            if (!TryReadString(bytes, ref cursor, allowNull: false, out var kind))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, allowNull: false, out var label))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, allowNull: true, out var detail))
            {
                break;
            }

            if ((long)cursor + 1 + 4 > bytes.Length)
            {
                break;
            }
            var custom = bytes[cursor] != 0;
            cursor += 1;

            var bodyLength = cursor - pos;
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, bodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            cursor += 4;

            _events.Add(new MonitoringEvent(id, tUtcMs, kind!, label!, detail, custom));
            maxId = Math.Max(maxId, id);
            pos = cursor;
        }

        if (pos != bytes.Length)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(pos);
        }

        _nextId = maxId + 1;
    }

    // A torn/corrupted length prefix can read back as any Int32, including a
    // value near Int32.MaxValue - widen to long before adding, or the bounds
    // check itself can overflow and wrap negative, defeating the check it is
    // guarding (same hazard PrivacyLog.LoadFromLog guards against).
    private static bool TryReadString(byte[] bytes, ref int cursor, bool allowNull, out string? value)
    {
        value = null;
        if ((long)cursor + 4 > bytes.Length)
        {
            return false;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
        cursor += 4;

        if (allowNull && length == NullDetailLength)
        {
            return true;
        }
        if (length < 0 || (long)cursor + length > bytes.Length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes, cursor, length);
        cursor += length;
        return true;
    }

    private void AppendRecord(MonitoringEvent ev)
    {
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        WriteRecord(fs, ev);
        fs.Flush(flushToDisk: true);
    }

    private static void WriteRecord(FileStream fs, MonitoringEvent ev)
    {
        var kindBytes = Encoding.UTF8.GetBytes(ev.Kind);
        var labelBytes = Encoding.UTF8.GetBytes(ev.Label);
        var detailBytes = ev.Detail is null ? null : Encoding.UTF8.GetBytes(ev.Detail);

        var bodyLength = 8 + 8
            + 4 + kindBytes.Length
            + 4 + labelBytes.Length
            + 4 + (detailBytes?.Length ?? 0)
            + 1;
        var buf = new byte[bodyLength + 4];

        var pos = 0;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), ev.Id);
        pos += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), ev.TUtcMs);
        pos += 8;

        pos = WriteString(buf, pos, kindBytes);
        pos = WriteString(buf, pos, labelBytes);

        if (detailBytes is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), NullDetailLength);
            pos += 4;
        }
        else
        {
            pos = WriteString(buf, pos, detailBytes);
        }

        buf[pos] = (byte)(ev.Custom ? 1 : 0);
        pos += 1;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static int WriteString(byte[] buf, int pos, byte[] valueBytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), valueBytes.Length);
        pos += 4;
        valueBytes.CopyTo(buf.AsSpan(pos));
        return pos + valueBytes.Length;
    }

    // Write-tmp-then-atomic-rename, the same crash-safety shape as
    // PrivacyLog.RewriteCompacted.
    private void RewriteCompacted()
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var ev in _events)
            {
                WriteRecord(fs, ev);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
