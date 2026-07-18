using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Mcp.History.Binary;

/// <summary>
/// Per-sensor identity and mutable snapshot (name/kind/unit/last value/last
/// seen) for BinaryAiHistoryStore. Unlike AppNameDictionary/EntityRegistry
/// (append-only, immutable once written), every field here can change on
/// every RecordSamples call, so this is not an append log: the
/// whole snapshot is rewritten (temp file, flush, atomic rename) each time
/// any entry changes, the same crash-safety shape as
/// AppUsageStore.PersistPruneFloor. A full rewrite can only ever leave the
/// old complete file (rename never happened) or the new complete one
/// (rename already happened) on disk, so - unlike the append logs elsewhere
/// in this store family - there is no torn-tail case to truncate on load,
/// only a defensive stop-at-first-invalid-record parse for a file corrupted
/// by something other than this class's own writes.
///
/// GlobalId is assigned in first-seen order and never reassigned; it is the
/// index BinaryAiHistoryStore uses into its per-sensor raw/1-minute/5-minute
/// ring arrays, the same role EntityRegistry's ring index plays for
/// GpuRingStore/FanRingStore.
/// </summary>
internal sealed class SensorMetaStore
{
    private sealed class Entry
    {
        public int GlobalId;
        public string Name = "";
        public string Kind = "";
        public string Unit = "";
        public double LastValue;
        public long LastSeenUtcMs;
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public SensorMetaStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Assigns a new GlobalId if sensorId is unseen, or keeps the
    /// existing one; either way, overwrites name/kind/unit/lastValue/
    /// lastSeenUtcMs with the values from this call, matching
    /// sensor_meta's unconditional upsert. Returns the sensor's
    /// GlobalId.</summary>
    public int RegisterOrUpdate(string sensorId, string name, string kind, string unit, double value, long tsUtcMs)
    {
        if (!_byId.TryGetValue(sensorId, out var entry))
        {
            entry = new Entry { GlobalId = _order.Count };
            _byId[sensorId] = entry;
            _order.Add(sensorId);
        }
        entry.Name = name;
        entry.Kind = kind;
        entry.Unit = unit;
        entry.LastValue = value;
        entry.LastSeenUtcMs = tsUtcMs;
        return entry.GlobalId;
    }

    /// <summary>The highest GlobalId assigned so far, or -1 if no sensor has
    /// ever been registered. GlobalIds are dense and assigned in _order's own
    /// sequence (index i always has GlobalId == i), so this is the array
    /// length every per-sensor ring array must be grown to cover, without a
    /// dictionary lookup per entry.</summary>
    public int MaxGlobalId => _order.Count - 1;

    public IReadOnlyList<string> KnownSensorIdsSorted() => _order.OrderBy(id => id, StringComparer.Ordinal).ToList();

    public readonly record struct SensorEntry(string SensorId, int GlobalId, string Name, string Unit, double LastValue, long LastSeenUtcMs);

    public SensorEntry? TryGetEntry(string sensorId) =>
        _byId.TryGetValue(sensorId, out var e) ? new SensorEntry(sensorId, e.GlobalId, e.Name, e.Unit, e.LastValue, e.LastSeenUtcMs) : null;

    public IEnumerable<SensorEntry> AllEntries()
    {
        foreach (var sensorId in _order)
        {
            var e = _byId[sensorId];
            yield return new SensorEntry(sensorId, e.GlobalId, e.Name, e.Unit, e.LastValue, e.LastSeenUtcMs);
        }
    }

    public void Persist()
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var sensorId in _order)
            {
                WriteRecord(fs, sensorId, _byId[sensorId]);
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static void WriteRecord(FileStream fs, string sensorId, Entry e)
    {
        var idBytes = Encoding.UTF8.GetBytes(sensorId);
        var nameBytes = Encoding.UTF8.GetBytes(e.Name);
        var kindBytes = Encoding.UTF8.GetBytes(e.Kind);
        var unitBytes = Encoding.UTF8.GetBytes(e.Unit);
        var bodyLength = 4 + idBytes.Length + 4 + nameBytes.Length + 4 + kindBytes.Length + 4 + unitBytes.Length + 8 + 8 + 4;
        var buf = new byte[bodyLength + 4];

        var pos = 0;
        WriteString(buf, ref pos, idBytes);
        WriteString(buf, ref pos, nameBytes);
        WriteString(buf, ref pos, kindBytes);
        WriteString(buf, ref pos, unitBytes);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(pos, 8), e.LastValue);
        pos += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), e.LastSeenUtcMs);
        pos += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), e.GlobalId);
        pos += 4;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
    }

    private static void WriteString(byte[] buf, ref int pos, byte[] bytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), bytes.Length);
        pos += 4;
        bytes.CopyTo(buf.AsSpan(pos));
        pos += bytes.Length;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(_path);
        var pos = 0;
        while (pos + 4 <= bytes.Length)
        {
            var cursor = pos;
            if (!TryReadString(bytes, ref cursor, out var sensorId))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, out var name))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, out var kind))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, out var unit))
            {
                break;
            }
            if ((long)cursor + 8 + 8 + 4 + 4 > bytes.Length)
            {
                break;
            }

            var lastValue = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            var lastSeenUtcMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;
            var globalId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
            cursor += 4;

            var bodyLength = cursor - pos;
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, bodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            cursor += 4;

            _byId[sensorId] = new Entry
            {
                GlobalId = globalId,
                Name = name,
                Kind = kind,
                Unit = unit,
                LastValue = lastValue,
                LastSeenUtcMs = lastSeenUtcMs,
            };
            _order.Add(sensorId);
            pos = cursor;
        }
    }

    private static bool TryReadString(byte[] bytes, ref int cursor, out string value)
    {
        value = "";
        if ((long)cursor + 4 > bytes.Length)
        {
            return false;
        }
        var len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
        cursor += 4;
        if (len < 0 || (long)cursor + len > bytes.Length)
        {
            return false;
        }
        value = Encoding.UTF8.GetString(bytes, cursor, len);
        cursor += len;
        return true;
    }
}
