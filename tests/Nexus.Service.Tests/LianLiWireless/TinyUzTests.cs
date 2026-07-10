using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Byte-level and round-trip tests for the TinyUZ LZ77 encoder/decoder. The
/// exact-byte vectors are computed by transliterating the upstream
/// sisong/tinyuz bit-writer (compress/tuz_enc_private/tuz_enc_code.cpp's
/// TTuzCode::outType/outLen/outDictPos/outDict/outCtrl) into a small Python
/// script and running it, not guessed or reverse-engineered from this port.
/// </summary>
public class TinyUzTests
{
    [Fact]
    public void Compress_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(() => TinyUz.Compress(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compress_header_carries_dict_size_4096_little_endian()
    {
        var encoded = TinyUz.Compress(new byte[] { 0x01 });
        Assert.Equal(0x00, encoded[0]);
        Assert.Equal(0x10, encoded[1]);
        Assert.Equal(0x00, encoded[2]);
        Assert.Equal(0x00, encoded[3]);
    }

    [Fact]
    public void Compress_single_byte_matches_hand_derived_bytes()
    {
        // Header (4B dictSize=4096 LE) + one control byte (0x19) packing the
        // literal type-bit, the stream-end length code, and the "no reuse"
        // flag + the literal byte + the trailing dict_pos=0 byte.
        var expected = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x19, 0xAB, 0x00 };
        var encoded = TinyUz.Compress(new byte[] { 0xAB });
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Compress_emits_a_back_reference_matching_the_reference_bit_writer()
    {
        // "ABCABC": 3 literal bytes (A,B,C, too short for a literal-line),
        // then one dict match (length 3, distance 3) covering the repeat,
        // then the stream-end control code. Bytes computed by the reference
        // bit-writer script described in the class summary.
        var expected = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x17, 0x41, 0x42, 0x43, 0x03, 0x06, 0x00 };
        var encoded = TinyUz.Compress(new byte[] { 0x41, 0x42, 0x43, 0x41, 0x42, 0x43 });
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void Decompress_handles_clip_end_control_code()
    {
        // Literal 'A', a clip-end control code (byte-aligns the type-bit
        // stream and continues decoding with no output), literal 'B', then
        // stream-end. This is a firmware-legal stream our own encoder never
        // emits (it never splits into clips); bytes computed by the
        // reference bit-writer script described in the class summary.
        var encoded = new byte[] { 0x00, 0x10, 0x00, 0x00, 0x09, 0x41, 0x00, 0x19, 0x42, 0x00 };
        var (dictSize, decoded) = TinyUz.Decompress(encoded);
        Assert.Equal(TinyUz.DictSize, dictSize);
        Assert.Equal(new byte[] { 0x41, 0x42 }, decoded);
    }

    [Fact]
    public void Compress_decompress_roundtrips_arbitrary_data()
    {
        var data = new byte[64];
        new Random(1234).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (dictSize, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(TinyUz.DictSize, dictSize);
        Assert.Equal(data, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(360)]
    public void Compress_decompress_roundtrips_at_type_byte_boundary_lengths(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(5000)]
    public void Compress_decompress_roundtrips_around_dict_size_boundary(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Compress_decompress_roundtrips_a_match_at_the_maximum_dict_distance()
    {
        // A DictSize-byte random head, then a 900-byte copy of its first 900
        // bytes appended: the tail's best match sits at distance exactly
        // DictSize (the largest distance the format allows), forcing the
        // multi-byte dict_pos code and the BigPosForLen length borrow.
        var head = new byte[TinyUz.DictSize];
        new Random(7).NextBytes(head);
        var data = new byte[TinyUz.DictSize + 900];
        Array.Copy(head, 0, data, 0, TinyUz.DictSize);
        Array.Copy(head, 0, data, TinyUz.DictSize, 900);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < data.Length, "the long-distance repeat should be found and shrink the output");
    }

    [Fact]
    public void Compress_solid_color_frame_compresses_well_under_original_size()
    {
        var data = new byte[360];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = 200;
            data[i + 1] = 100;
            data[i + 2] = 50;
        }

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < 50, $"expected well under 360 bytes, got {encoded.Length}");
    }

    [Fact]
    public void Compress_gradient_pattern_compresses_meaningfully()
    {
        // 40 LEDs of a smooth RGB gradient (4 LEDs per shade step), matching
        // the repeat-heavy shape of a real ring animation.
        var data = new byte[40 * 3];
        for (var led = 0; led < 40; led++)
        {
            var shade = (byte)(led / 4);
            data[led * 3] = shade;
            data[led * 3 + 1] = (byte)(255 - shade);
            data[led * 3 + 2] = shade;
        }

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length < data.Length / 2, $"expected a meaningful reduction from {data.Length} bytes, got {encoded.Length}");
    }

    [Fact]
    public void Compress_realistic_incompressible_frame_stays_under_max_compressed_length()
    {
        // A DictSize-sized frame of random (worst-case incompressible) bytes
        // is far larger than any real animation frame the firmware receives.
        var data = new byte[TinyUz.DictSize];
        new Random(99).NextBytes(data);

        var encoded = TinyUz.Compress(data);
        var (_, decoded) = TinyUz.Decompress(encoded);

        Assert.Equal(data, decoded);
        Assert.True(encoded.Length <= TinyUz.MaxCompressedLength);
    }

    [Fact]
    public void Compress_throws_when_output_exceeds_max_compressed_length()
    {
        // Random (incompressible) data comfortably larger than
        // MaxCompressedLength: even the byte-aligned literal-line path (about
        // 8 bits/byte plus a small fixed overhead) cannot fit this many
        // source bytes under the cap.
        var data = new byte[TinyUz.MaxCompressedLength + 4096];
        new Random(555).NextBytes(data);
        Assert.Throws<InvalidOperationException>(() => TinyUz.Compress(data));
    }
}
