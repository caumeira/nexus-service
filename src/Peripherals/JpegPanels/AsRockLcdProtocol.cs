using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The ASRock LCD's control channel. Image frames go out as plain chunked reports
/// (<see cref="JpegPanelHeaderStyle.AsRockFramed"/>), but everything else - connect, enable
/// the real-time display, disconnect - is an HTTP-shaped text message wrapped in an escaped,
/// checksummed 0x5A frame.
///
/// Protocol reconstructed from third-party documentation. No unit has been run against it.
/// </summary>
public static class AsRockLcdProtocol
{
    /// <summary>Frame delimiter, and the byte the escaping exists to keep out of the body.</summary>
    public const byte FrameMarker = 0x5A;

    /// <summary>Escape prefix: 0x5A becomes 5B 01 and 0x5B becomes 5B 02.</summary>
    public const byte EscapeMarker = 0x5B;

    /// <summary>
    /// Builds the message text. The wire form is "{method} {command} 1", then Key=Value
    /// headers, a blank line, and an optional JSON body - CRLF throughout.
    /// </summary>
    public static string BuildMessage(string method, string command, int sequence, long unixMs, string? jsonBody)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(command).Append(" 1\r\n");
        sb.Append("SeqNumber=").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Date=").Append(unixMs.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        if (jsonBody is not null)
        {
            sb.Append("ContentType=json\r\n");
            sb.Append("ContentLength=").Append(jsonBody.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        sb.Append("\r\n");
        if (jsonBody is not null)
        {
            sb.Append(jsonBody);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wraps a payload in the 0x5A frame and applies escaping. Layout before escaping:
    /// a 0x00 report id, the 0x5A marker, a big-endian length, the payload, a checksum, and
    /// a closing 0x5A. The length counts everything from the marker to the closing byte -
    /// not the report id - which is why it is payload length plus five.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("payload cannot be empty", nameof(payload));
        }
        int frameLength = 1 + 2 + payload.Length + 1 + 1;
        var plain = new byte[frameLength + 1];
        int w = 0;
        plain[w++] = 0x00;
        plain[w++] = FrameMarker;
        plain[w++] = (byte)((frameLength >> 8) & 0xFF);
        plain[w++] = (byte)(frameLength & 0xFF);
        payload.CopyTo(plain.AsSpan(w));
        w += payload.Length;

        int checksum = ((frameLength >> 8) & 0xFF) + (frameLength & 0xFF);
        for (int i = 0; i < payload.Length; i++)
        {
            checksum += payload[i];
        }
        plain[w++] = (byte)(checksum & 0xFF);
        plain[w] = FrameMarker;

        return Escape(plain);
    }

    /// <summary>
    /// Escapes everything except the two leading bytes and the closing marker, so the frame
    /// delimiters stay unambiguous while the body can contain their values.
    /// </summary>
    public static byte[] Escape(ReadOnlySpan<byte> frame)
    {
        int extra = 0;
        for (int i = 2; i < frame.Length - 1; i++)
        {
            if (frame[i] == FrameMarker || frame[i] == EscapeMarker)
            {
                extra++;
            }
        }
        var escaped = new byte[frame.Length + extra];
        int w = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            if (i < 2 || i == frame.Length - 1)
            {
                escaped[w++] = frame[i];
                continue;
            }
            if (frame[i] == FrameMarker)
            {
                escaped[w++] = EscapeMarker;
                escaped[w++] = 0x01;
            }
            else if (frame[i] == EscapeMarker)
            {
                escaped[w++] = EscapeMarker;
                escaped[w++] = 0x02;
            }
            else
            {
                escaped[w++] = frame[i];
            }
        }
        return escaped;
    }

    /// <summary>
    /// Recovers the message text from a reply. Skips the two-byte header, stops at the
    /// closing marker, unescapes, and drops the two length bytes and the checksum.
    /// Returns an empty span when the reply is too short to be a frame.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 6)
        {
            return Array.Empty<byte>();
        }
        var body = new List<byte>(reply.Length);
        for (int i = 2; i < reply.Length; i++)
        {
            if (reply[i] == FrameMarker)
            {
                break;
            }
            body.Add(reply[i]);
        }
        var unescaped = Unescape(body);
        // Two length bytes at the front, one checksum at the back.
        return unescaped.Length <= 3 ? Array.Empty<byte>() : unescaped[2..^1];
    }

    public static byte[] Unescape(IReadOnlyList<byte> data)
    {
        var output = new byte[data.Count];
        int w = 0;
        for (int i = 0; i < data.Count; i++)
        {
            if (data[i] == EscapeMarker && i + 1 < data.Count)
            {
                i++;
                output[w++] = data[i] == 0x01 ? FrameMarker : EscapeMarker;
            }
            else
            {
                output[w++] = data[i];
            }
        }
        return output[..w];
    }

    /// <summary>
    /// Status code from a decoded reply. The first line is "{proto} {status} ...", so the
    /// status is its second field. Null when the reply is not a status line.
    /// </summary>
    public static string? ParseStatus(ReadOnlySpan<byte> decoded)
    {
        if (decoded.IsEmpty)
        {
            return null;
        }
        var text = Encoding.ASCII.GetString(decoded);
        var firstLine = text.Split("\r\n", StringSplitOptions.None)[0];
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>
    /// True when a decoded connect reply says the panel has finished booting. The panel
    /// accepts frames only after that, and answers the connect message either way.
    /// </summary>
    public static bool ParseBootFinished(ReadOnlySpan<byte> decoded)
    {
        if (decoded.IsEmpty)
        {
            return false;
        }
        var text = Encoding.ASCII.GetString(decoded);
        var marker = text.IndexOf("\"bootFinish\"", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }
        var colon = text.IndexOf(':', marker);
        if (colon < 0)
        {
            return false;
        }
        for (int i = colon + 1; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                continue;
            }
            return text[i] == '1';
        }
        return false;
    }
}
