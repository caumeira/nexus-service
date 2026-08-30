using System;
using System.Linq;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// Pins the chunk framing for every JPEG-over-HID cooler LCD. The byte layouts were
/// reconstructed from third-party documentation and no unit exists to check them against,
/// so these tests guard the reconstruction, not the hardware's acceptance of it.
/// </summary>
public class JpegPanelProtocolTests
{
    private static byte[] Jpeg(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 251);
        }
        return data;
    }

    // ── Lian Li Galahad II LCD ──

    [Fact]
    public void LianLi_header_carries_big_endian_total_length_and_sequence()
    {
        var jpeg = Jpeg(3000);
        var report = new byte[1024];

        var written = JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.LianLiSequenced, 0x0E, jpeg, offset: 0, chunkIndex: 0);

        Assert.Equal(1013, written);
        Assert.Equal(0x02, report[0]);
        Assert.Equal(0x0E, report[1]);
        // 3000 = 0x00000BB8, big-endian.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x0B, 0xB8 }, report[2..6]);
        // Sequence 0 across three bytes.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00 }, report[6..9]);
        // Chunk length 1013 = 0x03F5, big-endian.
        Assert.Equal(new byte[] { 0x03, 0xF5 }, report[9..11]);
        Assert.Equal(jpeg.Take(1013), report[11..].ToArray());
    }

    [Fact]
    public void LianLi_second_chunk_advances_the_sequence_and_repeats_the_total()
    {
        var jpeg = Jpeg(3000);
        var report = new byte[1024];

        JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.LianLiSequenced, 0x0E, jpeg, offset: 1013, chunkIndex: 1);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x0B, 0xB8 }, report[2..6]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, report[6..9]);
        Assert.Equal(jpeg.Skip(1013).Take(1013), report[11..].ToArray());
    }

    [Fact]
    public void LianLi_last_chunk_reports_the_short_length_and_zero_pads()
    {
        var jpeg = Jpeg(2030); // 1013 + 1013 + 4
        var report = new byte[1024];

        var written = JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.LianLiSequenced, 0x0E, jpeg, offset: 2026, chunkIndex: 2);

        Assert.Equal(4, written);
        Assert.Equal(new byte[] { 0x00, 0x04 }, report[9..11]);
        Assert.All(report[(11 + 4)..], b => Assert.Equal(0, b));
    }

    // ── Corsair XC7 / Elite Capellix ──

    [Fact]
    public void Corsair_header_flags_the_last_chunk_and_writes_length_little_endian()
    {
        var jpeg = Jpeg(2000);
        var report = new byte[1024];

        JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.CorsairChunked, 0x1F, jpeg, offset: 0, chunkIndex: 0);

        Assert.Equal(new byte[] { 0x02, 0x05, 0x1F, 0x00, 0x00, 0x00 }, report[0..6]);
        // 1016 = 0x03F8, low byte first - the opposite order from Lian Li.
        Assert.Equal(new byte[] { 0xF8, 0x03 }, report[6..8]);

        var last = new byte[1024];
        var written = JpegPanelProtocol.FillChunk(
            last, JpegPanelHeaderStyle.CorsairChunked, 0x1F, jpeg, offset: 1016, chunkIndex: 1);

        Assert.Equal(984, written);
        Assert.Equal(0x01, last[3]);
        Assert.Equal(0x01, last[4]);
        Assert.Equal(new byte[] { 0xD8, 0x03 }, last[6..8]);
    }

    [Fact]
    public void Corsair_selector_distinguishes_xc7_from_capellix()
    {
        var jpeg = Jpeg(10);
        var xc7 = new byte[1024];
        var capellix = new byte[1024];

        JpegPanelProtocol.FillChunk(xc7, JpegPanelHeaderStyle.CorsairChunked, 0x1F, jpeg, 0, 0);
        JpegPanelProtocol.FillChunk(capellix, JpegPanelHeaderStyle.CorsairChunked, 0x40, jpeg, 0, 0);

        Assert.Equal(0x1F, xc7[2]);
        Assert.Equal(0x40, capellix[2]);
    }

    [Fact]
    public void Corsair_single_chunk_frame_is_marked_last_immediately()
    {
        var report = new byte[1024];

        var written = JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.CorsairChunked, 0x40, Jpeg(64), 0, 0);

        Assert.Equal(64, written);
        Assert.Equal(0x01, report[3]);
    }

    // ── ID-Cooling FX-LCD ──

    [Fact]
    public void IdCooling_first_report_is_tagged_and_counts_its_own_framing()
    {
        var jpeg = Jpeg(5000);
        var report = new byte[1025];

        var written = JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.IdCoolingTagged, 0x00, jpeg, offset: 0, chunkIndex: 0);

        Assert.Equal(992, written);
        Assert.Equal(0x00, report[0]);
        // ASCII "CRT" then "DRA".
        Assert.Equal(new byte[] { 0x43, 0x52, 0x54 }, report[1..4]);
        Assert.Equal(new byte[] { 0x44, 0x52, 0x41 }, report[6..9]);
        // The length field is the JPEG plus 32 bytes of framing, big-endian in two bytes.
        var framed = 5000 + 32;
        Assert.Equal((byte)((framed >> 8) & 0xFF), report[11]);
        Assert.Equal((byte)(framed & 0xFF), report[12]);
        Assert.Equal(0xB1, report[13]);
        Assert.Equal(jpeg.Take(992), report[33..].ToArray());
    }

    [Fact]
    public void IdCooling_later_reports_are_bare_payload_behind_the_report_id()
    {
        var jpeg = Jpeg(5000);
        var report = new byte[1025];

        var written = JpegPanelProtocol.FillChunk(
            report, JpegPanelHeaderStyle.IdCoolingTagged, 0x00, jpeg, offset: 992, chunkIndex: 1);

        Assert.Equal(1024, written);
        Assert.Equal(0x00, report[0]);
        // No tag on a continuation report - the first payload byte follows the report id.
        Assert.Equal(jpeg.Skip(992).Take(1024), report[1..].ToArray());
    }

    // ── shared behaviour ──

    [Theory]
    [InlineData(JpegPanelHeaderStyle.LianLiSequenced, 1024, 1013, 1)]
    [InlineData(JpegPanelHeaderStyle.LianLiSequenced, 1024, 1014, 2)]
    [InlineData(JpegPanelHeaderStyle.CorsairChunked, 1024, 1016, 1)]
    [InlineData(JpegPanelHeaderStyle.CorsairChunked, 1024, 1017, 2)]
    // ID-Cooling's first report holds less than the rest, so its counts are not a
    // straight division.
    [InlineData(JpegPanelHeaderStyle.IdCoolingTagged, 1025, 992, 1)]
    [InlineData(JpegPanelHeaderStyle.IdCoolingTagged, 1025, 993, 2)]
    [InlineData(JpegPanelHeaderStyle.IdCoolingTagged, 1025, 2016, 2)]
    [InlineData(JpegPanelHeaderStyle.IdCoolingTagged, 1025, 2017, 3)]
    public void Chunk_count_matches_what_filling_actually_consumes(
        JpegPanelHeaderStyle style, int reportLength, int jpegLength, int expected)
    {
        Assert.Equal(expected, JpegPanelProtocol.ChunkCount(style, reportLength, jpegLength));

        // Cross-check against the encoder itself rather than trusting the arithmetic twice.
        var jpeg = Jpeg(jpegLength);
        var report = new byte[reportLength];
        int offset = 0, chunks = 0;
        while (offset < jpeg.Length)
        {
            offset += JpegPanelProtocol.FillChunk(report, style, 0x00, jpeg, offset, chunks);
            chunks++;
        }
        Assert.Equal(expected, chunks);
    }

    [Fact]
    public void Empty_frame_needs_no_reports()
    {
        Assert.Equal(0, JpegPanelProtocol.ChunkCount(JpegPanelHeaderStyle.CorsairChunked, 1024, 0));
    }

    /// <summary>
    /// Every style clears the buffer first, so a short final chunk cannot leak the previous
    /// frame's tail into the padding - the panels read the declared length, but a device that
    /// reads the whole report would otherwise see stale pixels.
    /// </summary>
    [Theory]
    [InlineData(JpegPanelHeaderStyle.LianLiSequenced, 1024)]
    [InlineData(JpegPanelHeaderStyle.CorsairChunked, 1024)]
    [InlineData(JpegPanelHeaderStyle.IdCoolingTagged, 1025)]
    public void Reuse_of_a_report_buffer_never_leaks_the_previous_chunk(
        JpegPanelHeaderStyle style, int reportLength)
    {
        var report = new byte[reportLength];
        report.AsSpan().Fill(0xEE);

        JpegPanelProtocol.FillChunk(report, style, 0x00, Jpeg(8), offset: 0, chunkIndex: 0);

        var header = JpegPanelProtocol.HeaderLength(style, isFirstChunk: true);
        Assert.All(report[(header + 8)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_report_shorter_than_its_header_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => JpegPanelProtocol.FillChunk(
            new byte[4], JpegPanelHeaderStyle.CorsairChunked, 0x40, Jpeg(16), 0, 0));
    }
}
