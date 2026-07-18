using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IPrivacySessionStore: a single append-only log file
/// plus an in-RAM dictionary keyed by (appId, capability, startUtcSec), the
/// key a session is uniquely identified by. Every Upsert both assigns the RAM
/// dictionary entry for that key (an insert or a close-in-place are the same
/// assignment) and appends one record capturing the session's new state, so
/// reconstructing the dictionary on open is nothing more than replaying every
/// record in file order and repeating that same assignment - the last record
/// for a key always wins, whether that replay happens live or during
/// recovery.
///
/// Privacy sessions are transition-driven and low-volume (PrivacyAccessWatcher
/// polls every few seconds but only Upserts on an actual open/close or its
/// hourly prune - see its class doc), unlike the 1Hz scalar/entity rings.
/// That call volume is why this store uses one plain lock around every
/// public method instead of RingFile's lock-free Volatile-swap discipline.
///
/// PruneOlderThan is the one operation that touches more than the RAM
/// dictionary: after removing every session IsStale flags, it rewrites the
/// whole log from the surviving sessions (write to a temp file, flush, then
/// atomic-rename over the original - the same crash-safety shape as
/// AppUsageStore.PersistPruneFloor) instead of leaving the removed sessions'
/// records dead in the file. Without that rewrite, a key that gets Upserted
/// repeatedly over the service's lifetime (a capability an app opens and
/// closes many times) would grow the log without bound even though only a
/// handful of sessions are ever live at once.
/// </summary>
internal sealed class PrivacyLog
{
    private readonly record struct SessionKey(string AppId, string Capability, long StartUtcSec);

    // No real session end is ever this value, so it doubles as "this session
    // is still open" on disk without a separate presence flag - the same
    // sentinel-over-flag convention ScalarRingStore/AppUsageStore use for
    // their own nullable fixed-width fields.
    private const long OpenSessionEndSentinel = long.MinValue;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<SessionKey, PrivacySession> _sessions = new();

    public PrivacyLog(string path)
    {
        _path = path;
        if (!File.Exists(_path))
        {
            using var created = File.Create(_path);
        }
        LoadFromLog();
    }

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec)
    {
        lock (_lock)
        {
            var session = new PrivacySession(appId, capability, startUtcSec, endUtcSec);
            // Disk before RAM: if AppendRecord throws (disk full, transient
            // I/O error), _sessions must not already reflect a state a crash
            // would then lose - the same order AppNameDictionary.RegisterOrGet
            // uses for its own append-then-publish record.
            AppendRecord(session);
            _sessions[new SessionKey(appId, capability, startUtcSec)] = session;
        }
    }

    public IReadOnlyList<PrivacySession> Query(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(s => s.StartUtcSec <= toSec && (s.EndUtcSec is null || s.EndUtcSec >= fromSec))
                .OrderBy(s => s.StartUtcSec)
                .ToList();
        }
    }

    public void PruneOlderThan(long cutoffSec)
    {
        lock (_lock)
        {
            var stale = _sessions.Where(kv => IsStale(kv.Value, cutoffSec)).Select(kv => kv.Key).ToList();
            if (stale.Count == 0)
            {
                return;
            }

            foreach (var key in stale)
            {
                _sessions.Remove(key);
            }
            RewriteCompacted();
        }
    }

    // A closed session is judged by its end, an open one (no end recorded
    // yet) by its start - a backstop for a session that never got a proper
    // close recorded (PrivacyAccessTransitions handles the reachable cases
    // directly; this is the fallback for any it doesn't).
    private static bool IsStale(PrivacySession session, long cutoffSec) =>
        session.EndUtcSec is { } end ? end < cutoffSec : session.StartUtcSec < cutoffSec;

    // Replays every whole, CRC-valid record in file order into _sessions,
    // the same assignment Upsert itself performs. Stops at the first record
    // that fails a bounds or CRC check - a crash mid-append leaves at most
    // one torn trailing record, the same recovery shape as
    // AppNameDictionary.Load - and truncates the file to that clean boundary
    // so a later Append lands right after the last good record instead of
    // behind unreachable garbage.
    private void LoadFromLog()
    {
        var bytes = File.ReadAllBytes(_path);
        var pos = 0;
        while (pos + 4 <= bytes.Length)
        {
            var cursor = pos;
            var appIdLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
            cursor += 4;
            // A torn/corrupted length prefix can read back as any Int32,
            // including a value near Int32.MaxValue - widen to long before
            // adding, or the bounds check itself can overflow and wrap
            // negative, defeating the check it's guarding.
            if (appIdLen < 0 || (long)cursor + appIdLen + 4 > bytes.Length)
            {
                break;
            }
            var appId = Encoding.UTF8.GetString(bytes, cursor, appIdLen);
            cursor += appIdLen;

            var capLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
            cursor += 4;
            if (capLen < 0 || (long)cursor + capLen + 8 + 8 + 4 > bytes.Length)
            {
                break;
            }
            var capability = Encoding.UTF8.GetString(bytes, cursor, capLen);
            cursor += capLen;

            var startUtcSec = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            var endOrSentinel = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;

            var bodyLength = cursor - pos;
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, bodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            cursor += 4;

            var endUtcSec = endOrSentinel == OpenSessionEndSentinel ? (long?)null : endOrSentinel;
            var session = new PrivacySession(appId, capability, startUtcSec, endUtcSec);
            _sessions[new SessionKey(appId, capability, startUtcSec)] = session;
            pos = cursor;
        }

        if (pos != bytes.Length)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(pos);
        }
    }

    private void AppendRecord(PrivacySession session)
    {
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);
        WriteRecord(fs, session);
        fs.Flush(flushToDisk: true);
    }

    private static void WriteRecord(FileStream fs, PrivacySession session)
    {
        var appIdBytes = Encoding.UTF8.GetBytes(session.AppId);
        var capBytes = Encoding.UTF8.GetBytes(session.Capability);
        var bodyLength = 4 + appIdBytes.Length + 4 + capBytes.Length + 8 + 8;
        var buf = new byte[bodyLength + 4];

        var pos = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), appIdBytes.Length);
        pos += 4;
        appIdBytes.CopyTo(buf.AsSpan(pos));
        pos += appIdBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), capBytes.Length);
        pos += 4;
        capBytes.CopyTo(buf.AsSpan(pos));
        pos += capBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), session.StartUtcSec);
        pos += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), session.EndUtcSec ?? OpenSessionEndSentinel);
        pos += 8;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    // Write-tmp-then-atomic-rename, the same crash-safety shape as
    // AppUsageStore.PersistPruneFloor: a crash mid-rewrite leaves either the
    // old complete log (rename never happened) or the new complete one
    // (rename already happened), never a torn file that becomes "the" log.
    private void RewriteCompacted()
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var session in _sessions.Values)
            {
                WriteRecord(fs, session);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
