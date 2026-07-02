using System;
using System.Buffers.Binary;

namespace Nexus.Service.Peripherals.Tryx.Panorama.Crypto;

/// <summary>
/// SM3 cryptographic hash (GM/T 0004-2012), pure C#. Used by <see cref="Sm2"/> for
/// its KDF and C3 digest, and directly by the Kanali cloud API's request signing.
/// 256-bit digest, 512-bit block, same Merlin-Damgard padding shape as SHA-256.
/// </summary>
public static class Sm3
{
    private static readonly uint[] Iv =
    {
        0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600,
        0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e,
    };

    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var v = (uint[])Iv.Clone();
        var padded = Pad(data);
        var w = new uint[68];
        var w1 = new uint[64];

        for (var blockOff = 0; blockOff < padded.Length; blockOff += 64)
        {
            ExpandBlock(padded.AsSpan(blockOff, 64), w, w1);
            Compress(v, w, w1);
        }

        var result = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), v[i]);
        }
        return result;
    }

    private static byte[] Pad(ReadOnlySpan<byte> msg)
    {
        var bitLen = (long)msg.Length * 8;
        var afterMarker = msg.Length + 1;
        var zeroPad = ((56 - afterMarker % 64) + 64) % 64;
        var total = afterMarker + zeroPad + 8;

        var result = new byte[total];
        msg.CopyTo(result);
        result[msg.Length] = 0x80;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(total - 8), bitLen);
        return result;
    }

    private static void ExpandBlock(ReadOnlySpan<byte> block, uint[] w, uint[] w1)
    {
        for (var j = 0; j < 16; j++)
        {
            w[j] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(j * 4, 4));
        }
        for (var j = 16; j < 68; j++)
        {
            var x = w[j - 16] ^ w[j - 9] ^ RotL(w[j - 3], 15);
            w[j] = P1(x) ^ RotL(w[j - 13], 7) ^ w[j - 6];
        }
        for (var j = 0; j < 64; j++)
        {
            w1[j] = w[j] ^ w[j + 4];
        }
    }

    private static void Compress(uint[] v, uint[] w, uint[] w1)
    {
        uint a = v[0], b = v[1], c = v[2], d = v[3];
        uint e = v[4], f = v[5], g = v[6], h = v[7];

        for (var j = 0; j < 64; j++)
        {
            var tj = j < 16 ? 0x79cc4519u : 0x7a879d8au;
            var ss1 = RotL(RotL(a, 12) + e + RotL(tj, j % 32), 7);
            var ss2 = ss1 ^ RotL(a, 12);
            var tt1 = Ff(j, a, b, c) + d + ss2 + w1[j];
            var tt2 = Gg(j, e, f, g) + h + ss1 + w[j];
            d = c;
            c = RotL(b, 9);
            b = a;
            a = tt1;
            h = g;
            g = RotL(f, 19);
            f = e;
            e = P0(tt2);
        }

        v[0] ^= a; v[1] ^= b; v[2] ^= c; v[3] ^= d;
        v[4] ^= e; v[5] ^= f; v[6] ^= g; v[7] ^= h;
    }

    private static uint Ff(int j, uint x, uint y, uint z)
        => j < 16 ? x ^ y ^ z : (x & y) | (x & z) | (y & z);

    private static uint Gg(int j, uint x, uint y, uint z)
        => j < 16 ? x ^ y ^ z : (x & y) | (~x & z);

    private static uint P0(uint x) => x ^ RotL(x, 9) ^ RotL(x, 17);

    private static uint P1(uint x) => x ^ RotL(x, 15) ^ RotL(x, 23);

    private static uint RotL(uint x, int n) => (x << n) | (x >> (32 - n));
}
