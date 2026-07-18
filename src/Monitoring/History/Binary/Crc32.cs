using System;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Table-based CRC-32 (the IEEE 802.3 / zlib / gzip polynomial, reflected
/// input and output). Hand-rolled rather than taking a package dependency:
/// the algorithm is a few lines, has no reflection or platform surface to
/// verify for AOT, and both RingFile slots and SuperBlock headers only need
/// one integrity check, not a choice of algorithms.
///
/// This is a non-cryptographic integrity check, not a security boundary: it
/// catches accidental corruption (a torn write between two on-disk versions
/// of the same slot - see RingFile) far more reliably than it would catch a
/// deliberately crafted collision.
/// </summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }
            table[i] = value;
        }
        return table;
    }

    /// <summary>Computes the CRC-32 of <paramref name="data"/>. Deterministic
    /// and pure - the same bytes always produce the same result, which is
    /// the only property RingFile/SuperBlock's torn-write detection relies
    /// on.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
