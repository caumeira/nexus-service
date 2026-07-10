using System;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Result of <see cref="AdbServerProtocol.TryParseStatus"/>: whether the adb
/// server accepted the preceding service request, and on FAIL, the server's
/// own error text.
/// </summary>
public readonly struct AdbServerStatusResult
{
    public required bool Ok { get; init; }
    public required string? FailMessage { get; init; }

    /// <summary>Bytes of the input buffer this result consumed.</summary>
    public required int BytesConsumed { get; init; }
}

/// <summary>
/// Pure helpers for the adb server wire protocol (the raw TCP socket on
/// 127.0.0.1:5037, distinct from adb.exe's own CLI parsing). No sockets here
/// so the framing and status parsing are unit-testable without a live server.
/// </summary>
public static class AdbServerProtocol
{
    private const int StatusLength = 4;
    private const int LengthFieldSize = 4;
    private const string OkayStatus = "OKAY";
    private const string FailStatus = "FAIL";

    /// <summary>
    /// Frames a service request as the adb server expects: a 4-hex-digit
    /// ASCII length prefix followed by the ASCII request text.
    /// </summary>
    public static byte[] EncodeRequest(string request)
    {
        var payload = Encoding.ASCII.GetBytes(request);
        if (payload.Length > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "adb server request exceeds 65535 bytes");
        }

        var framed = new byte[LengthFieldSize + payload.Length];
        Encoding.ASCII.GetBytes(payload.Length.ToString("x4", CultureInfo.InvariantCulture)).CopyTo(framed, 0);
        payload.CopyTo(framed, LengthFieldSize);
        return framed;
    }

    /// <summary>
    /// Parses an adb server status reply (OKAY, or FAIL followed by its own
    /// hex4 length + message body) from the front of <paramref name="buffer"/>.
    /// Returns false when the buffer does not yet hold a complete status, so
    /// callers can keep accumulating bytes from the socket and retry.
    /// </summary>
    public static bool TryParseStatus(ReadOnlySpan<byte> buffer, out AdbServerStatusResult result)
    {
        result = default;
        if (buffer.Length < StatusLength)
        {
            return false;
        }

        var status = Encoding.ASCII.GetString(buffer[..StatusLength]);
        if (status == OkayStatus)
        {
            result = new AdbServerStatusResult { Ok = true, FailMessage = null, BytesConsumed = StatusLength };
            return true;
        }
        if (status != FailStatus)
        {
            throw new FormatException($"unexpected adb server status bytes: {status}");
        }

        if (buffer.Length < StatusLength + LengthFieldSize)
        {
            return false;
        }

        var lengthHex = Encoding.ASCII.GetString(buffer.Slice(StatusLength, LengthFieldSize));
        if (!int.TryParse(lengthHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var messageLength))
        {
            throw new FormatException($"invalid adb FAIL length prefix: {lengthHex}");
        }

        var total = StatusLength + LengthFieldSize + messageLength;
        if (buffer.Length < total)
        {
            return false;
        }

        var message = Encoding.ASCII.GetString(buffer.Slice(StatusLength + LengthFieldSize, messageLength));
        result = new AdbServerStatusResult { Ok = false, FailMessage = message, BytesConsumed = total };
        return true;
    }
}

/// <summary>
/// Detects the ASCII "READY" marker across arbitrary chunk boundaries from a
/// live socket read loop. Stateful: feed every received chunk in order.
/// </summary>
public sealed class ReadyScanner
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("READY");

    private int _matched;

    public bool Seen { get; private set; }

    /// <summary>
    /// Feeds one chunk of received bytes. Returns true once the marker has
    /// been seen (including on a call before this one).
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> chunk)
    {
        if (Seen)
        {
            return true;
        }

        foreach (var b in chunk)
        {
            if (b == Marker[_matched])
            {
                _matched++;
                if (_matched == Marker.Length)
                {
                    Seen = true;
                    return true;
                }
            }
            else
            {
                // "READY" has no repeated characters, so a mismatch can only
                // restart the match at position 0 or, if the mismatched byte
                // equals the marker's first byte, at position 1.
                _matched = b == Marker[0] ? 1 : 0;
            }
        }

        return false;
    }
}
