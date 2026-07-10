using System;
using System.Text;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class AdbServerProtocolTests
{
    [Fact]
    public void EncodeRequest_prefixes_4_hex_digit_ascii_length()
    {
        var framed = AdbServerProtocol.EncodeRequest("shell:echo hi");

        var prefix = Encoding.ASCII.GetString(framed, 0, 4);
        Assert.Equal("000d", prefix);
        Assert.Equal("shell:echo hi", Encoding.ASCII.GetString(framed, 4, framed.Length - 4));
    }

    [Fact]
    public void EncodeRequest_empty_string_encodes_zero_length()
    {
        var framed = AdbServerProtocol.EncodeRequest("");

        Assert.Equal(4, framed.Length);
        Assert.Equal("0000", Encoding.ASCII.GetString(framed));
    }

    [Fact]
    public void EncodeRequest_length_prefix_is_lowercase_hex()
    {
        var request = new string('a', 0x100);
        var framed = AdbServerProtocol.EncodeRequest(request);

        Assert.Equal("0100", Encoding.ASCII.GetString(framed, 0, 4));
    }

    [Fact]
    public void TryParseStatus_incomplete_buffer_returns_false()
    {
        var buffer = Encoding.ASCII.GetBytes("OK");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out _);

        Assert.False(complete);
    }

    [Fact]
    public void TryParseStatus_okay_returns_ok_result()
    {
        var buffer = Encoding.ASCII.GetBytes("OKAY");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out var result);

        Assert.True(complete);
        Assert.True(result.Ok);
        Assert.Null(result.FailMessage);
        Assert.Equal(4, result.BytesConsumed);
    }

    [Fact]
    public void TryParseStatus_okay_ignores_trailing_bytes()
    {
        var buffer = Encoding.ASCII.GetBytes("OKAYtrailing");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out var result);

        Assert.True(complete);
        Assert.True(result.Ok);
        Assert.Equal(4, result.BytesConsumed);
    }

    [Fact]
    public void TryParseStatus_fail_without_length_bytes_yet_returns_false()
    {
        var buffer = Encoding.ASCII.GetBytes("FAIL00");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out _);

        Assert.False(complete);
    }

    [Fact]
    public void TryParseStatus_fail_without_full_message_yet_returns_false()
    {
        var buffer = Encoding.ASCII.GetBytes("FAIL000adevi");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out _);

        Assert.False(complete);
    }

    [Fact]
    public void TryParseStatus_fail_parses_hex4_length_and_message_body()
    {
        var message = "device not found";
        var lengthHex = message.Length.ToString("x4");
        var buffer = Encoding.ASCII.GetBytes($"FAIL{lengthHex}{message}");

        var complete = AdbServerProtocol.TryParseStatus(buffer, out var result);

        Assert.True(complete);
        Assert.False(result.Ok);
        Assert.Equal(message, result.FailMessage);
        Assert.Equal(buffer.Length, result.BytesConsumed);
    }

    [Fact]
    public void TryParseStatus_unexpected_status_bytes_throw()
    {
        var buffer = Encoding.ASCII.GetBytes("NOPE");

        Assert.Throws<FormatException>(() => AdbServerProtocol.TryParseStatus(buffer, out _));
    }

    [Fact]
    public void ReadyScanner_marker_in_single_chunk_is_seen()
    {
        var scanner = new ReadyScanner();

        var seen = scanner.Feed(Encoding.ASCII.GetBytes("READY"));

        Assert.True(seen);
        Assert.True(scanner.Seen);
    }

    [Fact]
    public void ReadyScanner_marker_split_across_chunk_boundary_is_seen()
    {
        var scanner = new ReadyScanner();

        var afterFirst = scanner.Feed(Encoding.ASCII.GetBytes("REA"));
        Assert.False(afterFirst);
        Assert.False(scanner.Seen);

        var afterSecond = scanner.Feed(Encoding.ASCII.GetBytes("DY"));

        Assert.True(afterSecond);
        Assert.True(scanner.Seen);
    }

    [Fact]
    public void ReadyScanner_marker_preceded_by_garbage_is_seen()
    {
        var scanner = new ReadyScanner();

        var seen = scanner.Feed(Encoding.ASCII.GetBytes("$ stty raw -echo\r\nREADY"));

        Assert.True(seen);
    }

    [Fact]
    public void ReadyScanner_marker_absent_is_not_seen()
    {
        var scanner = new ReadyScanner();

        var seen = scanner.Feed(Encoding.ASCII.GetBytes("not the marker"));

        Assert.False(seen);
        Assert.False(scanner.Seen);
    }

    [Fact]
    public void ReadyScanner_partial_prefix_followed_by_unrelated_bytes_does_not_false_positive()
    {
        var scanner = new ReadyScanner();

        scanner.Feed(Encoding.ASCII.GetBytes("REA"));
        var seen = scanner.Feed(Encoding.ASCII.GetBytes("LLY NOT IT"));

        Assert.False(seen);
        Assert.False(scanner.Seen);
    }

    [Fact]
    public void ReadyScanner_once_seen_stays_seen_on_further_feeds()
    {
        var scanner = new ReadyScanner();
        scanner.Feed(Encoding.ASCII.GetBytes("READY"));

        var stillSeen = scanner.Feed(Encoding.ASCII.GetBytes("more data after"));

        Assert.True(stillSeen);
        Assert.True(scanner.Seen);
    }

    [Fact]
    public void ReadyScanner_marker_with_overlapping_false_start_is_seen()
    {
        var scanner = new ReadyScanner();

        var seen = scanner.Feed(Encoding.ASCII.GetBytes("REREADY"));

        Assert.True(seen);
    }
}
