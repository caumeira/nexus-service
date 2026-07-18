using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Service.Monitoring.History.Binary;

namespace Nexus.Service.Mcp.History.Binary;

/// <summary>
/// Append-only audit log for BinaryAiHistoryStore's RecordEvent/QueryEvents -
/// the binary equivalent of SqliteAiHistoryStore's events table. Unlike
/// PrivacyLog, there is no key to upsert on and so no compaction pass: an
/// event is immutable once recorded (matching the SQL table, which has no
/// UPDATE path either) and SqliteAiHistoryStore itself never prunes this
/// table, so neither does this. Every record is kept in RAM (a plain list,
/// replayed from the log on open) - event volume is bounded by how often an
/// MCP tool call happens, not by a sampling tick, the same low-volume
/// reasoning PrivacyLog's own class doc gives for its single lock.
/// </summary>
internal sealed class AiEventLog
{
    private readonly string _path;
    private readonly List<AiHistoryEventRow> _events = new();

    public AiEventLog(string path)
    {
        _path = path;
        if (!File.Exists(_path))
        {
            using var created = File.Create(_path);
        }
        Load();
    }

    public void Append(AiHistoryEventRow row)
    {
        AppendRecord(row);
        _events.Add(row);
    }

    public AiHistoryEventQueryResult QueryEvents(long fromUtcMs, string? type, int limit)
    {
        var matches = _events
            .Where(e => e.TsUtcMs >= fromUtcMs && (type is null || e.Kind == type))
            .OrderByDescending(e => e.TsUtcMs)
            .ToList();

        var truncated = matches.Count > limit;
        var page = matches.Take(Math.Max(limit, 0)).ToList();
        return new AiHistoryEventQueryResult(page, truncated);
    }

    private void Load()
    {
        var bytes = File.ReadAllBytes(_path);
        var pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            var cursor = pos;
            var tsUtcMs = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(cursor, 8));
            cursor += 8;

            if (!TryReadString(bytes, ref cursor, out var kind))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, out var name))
            {
                break;
            }
            if (!TryReadString(bytes, ref cursor, out var argsJson))
            {
                break;
            }
            if ((long)cursor + 1 + 4 > bytes.Length)
            {
                break;
            }
            var success = bytes[cursor] != 0;
            cursor += 1;

            var errorTextLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
            cursor += 4;
            string? errorText = null;
            if (errorTextLen >= 0)
            {
                if ((long)cursor + errorTextLen + 4 > bytes.Length)
                {
                    break;
                }
                errorText = Encoding.UTF8.GetString(bytes, cursor, errorTextLen);
                cursor += errorTextLen;
            }
            else if ((long)cursor + 4 > bytes.Length)
            {
                break;
            }

            var bodyLength = cursor - pos;
            var expectedCrc = Crc32.Compute(bytes.AsSpan(pos, bodyLength));
            var actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
            if (actualCrc != expectedCrc)
            {
                break;
            }
            cursor += 4;

            _events.Add(new AiHistoryEventRow(tsUtcMs, kind, name, argsJson, success, errorText));
            pos = cursor;
        }

        if (pos != bytes.Length)
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(pos);
        }
    }

    private void AppendRecord(AiHistoryEventRow row)
    {
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        fs.Seek(0, SeekOrigin.End);

        var kindBytes = Encoding.UTF8.GetBytes(row.Kind);
        var nameBytes = Encoding.UTF8.GetBytes(row.Name);
        var argsBytes = Encoding.UTF8.GetBytes(row.ArgsJson);
        var errorBytes = row.ErrorText is null ? null : Encoding.UTF8.GetBytes(row.ErrorText);

        var bodyLength = 8
            + 4 + kindBytes.Length
            + 4 + nameBytes.Length
            + 4 + argsBytes.Length
            + 1
            + 4 + (errorBytes?.Length ?? 0);
        var buf = new byte[bodyLength + 4];

        var pos = 0;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos, 8), row.TsUtcMs);
        pos += 8;
        WriteString(buf, ref pos, kindBytes);
        WriteString(buf, ref pos, nameBytes);
        WriteString(buf, ref pos, argsBytes);
        buf[pos] = (byte)(row.Success ? 1 : 0);
        pos += 1;
        if (errorBytes is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), -1);
            pos += 4;
        }
        else
        {
            WriteString(buf, ref pos, errorBytes);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLength, 4), Crc32.Compute(buf.AsSpan(0, bodyLength)));
        fs.Write(buf);
        fs.Flush(flushToDisk: true);
    }

    private static void WriteString(byte[] buf, ref int pos, byte[] bytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), bytes.Length);
        pos += 4;
        bytes.CopyTo(buf.AsSpan(pos));
        pos += bytes.Length;
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
