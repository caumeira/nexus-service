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
}
