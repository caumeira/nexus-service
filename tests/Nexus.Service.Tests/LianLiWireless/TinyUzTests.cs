using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Byte-level tests for the TinyUZ literal-only encoder. The single-byte
/// vector in <see cref="Compress_single_byte_matches_hand_derived_bytes"/> is
/// hand-derived from phstudy/uni-wireless-sync's tinyuz.py bit-writer (the
/// reference the plan cites as hardware-tested), not guessed.
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

    [Fact]
    public void Compress_throws_when_output_exceeds_max_compressed_length()
    {
        // Literal-only encoding costs roughly 9 output bytes per 8 input
        // bytes, so a buffer well past 8/9 * MaxCompressedLength overflows it.
        var data = new byte[TinyUz.MaxCompressedLength];
        Assert.Throws<InvalidOperationException>(() => TinyUz.Compress(data));
    }
}
