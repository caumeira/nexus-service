using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Parses the RK panel's stored-media list, which it pushes unsolicited on its
/// usbprint IN endpoint on connect (protobuf, UTF-8 file paths). Rather than
/// decode the protobuf framing, this scans the decoded text for
/// "default_&lt;digits&gt;.mp4" occurrences, which is all the preset filter needs.
/// </summary>
public static class TryxMediaList
{
    private const string StoreDirMarker = "/userdata/default/";
    private const string PresetPrefix = "default_";
    private const string VideoExtension = ".mp4";

    /// <summary>Distinct wallpaper preset ids (e.g. "default_01") found in
    /// <paramref name="data"/>, sorted ascending. Empty if the buffer is not the
    /// media-list payload (does not contain <see cref="StoreDirMarker"/>).</summary>
    public static IReadOnlyList<string> ParsePresetIds(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var text = Encoding.UTF8.GetString(data);
        if (!text.Contains(StoreDirMarker, StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var searchStart = 0;
        while (true)
        {
            var prefixIndex = text.IndexOf(PresetPrefix, searchStart, StringComparison.Ordinal);
            if (prefixIndex < 0)
            {
                break;
            }

            var digitsStart = prefixIndex + PresetPrefix.Length;
            var digitsEnd = digitsStart;
            while (digitsEnd < text.Length && char.IsAsciiDigit(text[digitsEnd]))
            {
                digitsEnd++;
            }
            searchStart = digitsEnd;

            var hasDigits = digitsEnd > digitsStart;
            var followedByExtension = digitsEnd + VideoExtension.Length <= text.Length
                && text.AsSpan(digitsEnd, VideoExtension.Length).SequenceEqual(VideoExtension);
            if (hasDigits && followedByExtension)
            {
                ids.Add(text[prefixIndex..digitsEnd]);
            }
        }

        return ids.Count == 0 ? Array.Empty<string>() : new List<string>(ids);
    }

    /// <summary>All media filenames the panel lists under <see cref="StoreDirMarker"/>
    /// (e.g. "default_01.mp4.h264_2240x1080", "download_44.mp4...", custom names), so the
    /// panel itself is the source of truth for what is stored - no local record needed.
    /// Empty if the buffer is not the media-list payload.</summary>
    public static IReadOnlyList<string> ParseMediaFilenames(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return Array.Empty<string>();
        var text = Encoding.UTF8.GetString(data);
        if (!text.Contains(StoreDirMarker, StringComparison.Ordinal)) return Array.Empty<string>();

        var names = new SortedSet<string>(StringComparer.Ordinal);
        var searchStart = 0;
        while (true)
        {
            var markerIndex = text.IndexOf(StoreDirMarker, searchStart, StringComparison.Ordinal);
            if (markerIndex < 0) break;
            var nameStart = markerIndex + StoreDirMarker.Length;
            var nameEnd = nameStart;
            // A filename runs until a control/non-printable byte or another path separator.
            while (nameEnd < text.Length && text[nameEnd] is not ('\0' or '\n' or '\r' or '/') && !char.IsControl(text[nameEnd]))
            {
                nameEnd++;
            }
            searchStart = nameEnd + 1;
            if (nameEnd > nameStart)
            {
                names.Add(text[nameStart..nameEnd]);
            }
        }
        return names.Count == 0 ? Array.Empty<string>() : new List<string>(names);
    }

    /// <summary>Bytes stored on the panel's /userdata, summed from the per-file sizes the
    /// media-list push carries: outer <c>f503 { repeated f2 { f1:path, f2:ext, f3:sizeBytes,
    /// f4:1 } }</c>. Returns null when the buffer is not a complete media-list frame (another
    /// IN read, or a list split across reads), so the caller keeps the last known total rather
    /// than resetting to 0. Only whole-frame reads count - a very large list that spills past a
    /// single drain read reports null until it fits.</summary>
    public static long? ParseMediaUsedBytes(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.IndexOf("/userdata/default/"u8) < 0) return null;
        if (!TryGetLenField(data, fieldNumber: 503, out var fileList)) return null;

        long total = 0;
        var any = false;
        var pos = 0;
        while (pos < fileList.Length)
        {
            if (!TryReadVarint(fileList, ref pos, out var tag)) break;
            var fn = (int)(tag >> 3);
            var wt = (int)(tag & 7);
            if (wt != WireLen)
            {
                if (!TrySkipField(fileList, ref pos, wt)) break;
                continue;
            }
            // Clamp against the remaining span BEFORE the int cast: a device-corrupt length
            // varint (bit 31 set, or huge) would make (int)len negative / overflow pos+len and
            // slip past a `pos + (int)len > Length` guard, then throw in Slice - and a throw here
            // kills the drain thread, re-arming the ~70s panel reset loop this transport avoids.
            if (!TryReadVarint(fileList, ref pos, out var len) || len > (ulong)(fileList.Length - pos)) break;
            var entry = fileList.Slice(pos, (int)len);
            pos += (int)len;
            if (fn != 2) continue; // repeated field 2 = one file entry
            var epos = 0;
            while (epos < entry.Length)
            {
                if (!TryReadVarint(entry, ref epos, out var etag)) break;
                if ((int)(etag >> 3) == 3 && (int)(etag & 7) == WireVarint)
                {
                    if (TryReadVarint(entry, ref epos, out var size)) { total += (long)size; any = true; }
                    break;
                }
                if (!TrySkipField(entry, ref epos, (int)(etag & 7))) break;
            }
        }
        return any ? total : null;
    }

    private const int WireVarint = 0;
    private const int WireLen = 2;

    private static bool TryReadVarint(ReadOnlySpan<byte> b, ref int pos, out ulong val)
    {
        val = 0;
        var shift = 0;
        while (pos < b.Length && shift < 64)
        {
            var x = b[pos++];
            val |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> b, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case WireVarint: return TryReadVarint(b, ref pos, out _);
            case 1: pos += 8; return pos <= b.Length;   // 64-bit
            case 5: pos += 4; return pos <= b.Length;   // 32-bit
            case WireLen:
                // Reject a length past the remaining span before casting - see the note in
                // ParseMediaUsedBytes; a negative (int)len here would leave pos negative and the
                // next varint read would index out of bounds and throw.
                if (!TryReadVarint(b, ref pos, out var len) || len > (ulong)(b.Length - pos)) return false;
                pos += (int)len;
                return true;
            default: return false;
        }
    }

    /// <summary>Content of the first length-delimited field matching <paramref name="fieldNumber"/>.</summary>
    private static bool TryGetLenField(ReadOnlySpan<byte> b, int fieldNumber, out ReadOnlySpan<byte> content)
    {
        content = default;
        var pos = 0;
        while (pos < b.Length)
        {
            if (!TryReadVarint(b, ref pos, out var tag)) return false;
            var fn = (int)(tag >> 3);
            var wt = (int)(tag & 7);
            if (wt == WireLen)
            {
                if (!TryReadVarint(b, ref pos, out var len) || len > (ulong)(b.Length - pos)) return false;
                if (fn == fieldNumber) { content = b.Slice(pos, (int)len); return true; }
                pos += (int)len;
            }
            else if (!TrySkipField(b, ref pos, wt))
            {
                return false;
            }
        }
        return false;
    }
}
