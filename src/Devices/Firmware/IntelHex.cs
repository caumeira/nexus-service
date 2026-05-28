using System;
using System.Globalization;
using System.IO;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Minimal Intel HEX parser. The bundled firmware images are Intel HEX text,
/// but dfu-util flashes raw binaries — so before a download we convert the
/// .hex to a flat <c>.bin</c> covering <see cref="BaseAddress"/>..
/// <see cref="EndAddress"/>, with gaps filled by 0xFF (erased-flash value).
///
/// Supports record types 00 (data), 01 (EOF), 04 (extended linear address),
/// 05 (start linear address — ignored). That's the full set HYTE's images use.
/// </summary>
public sealed class IntelHexImage
{
    public required uint BaseAddress { get; init; }

    /// <summary>Flat image from <see cref="BaseAddress"/>; gaps between records are 0xFF.</summary>
    public required byte[] Data { get; init; }

    /// <summary>First address past the image (BaseAddress + Data.Length).</summary>
    public uint EndAddress => BaseAddress + (uint)Data.Length;
}

public static class IntelHex
{
    public static IntelHexImage Parse(Stream hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        using var reader = new StreamReader(hex);
        return Parse(reader);
    }

    public static IntelHexImage Parse(string hexText) => Parse(new StringReader(hexText));

    private static IntelHexImage Parse(TextReader reader)
    {
        // First pass collects absolute (address, bytes) spans; we don't know the
        // base until we've seen the lowest data record, so buffer then flatten.
        var records = new System.Collections.Generic.List<(uint addr, byte[] data)>();
        uint upper = 0;            // extended linear address (record type 04), upper 16 bits
        uint min = uint.MaxValue;
        uint max = 0;

        string? line;
        var lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] != ':') throw new InvalidDataException($"Intel HEX line {lineNo} does not start with ':'.");

            // :LL AAAA TT [DD..] CC  — all hex, byte-count LL covers data only.
            var bytes = HexToBytes(line.AsSpan(1), lineNo);
            if (bytes.Length < 5) throw new InvalidDataException($"Intel HEX line {lineNo} too short.");

            int count = bytes[0];
            if (bytes.Length != count + 5) throw new InvalidDataException($"Intel HEX line {lineNo} length mismatch (declared {count}).");

            byte sum = 0;
            foreach (var b in bytes) sum += b;
            if (sum != 0) throw new InvalidDataException($"Intel HEX line {lineNo} checksum invalid.");

            uint offset = (uint)((bytes[1] << 8) | bytes[2]);
            int type = bytes[3];

            switch (type)
            {
                case 0x00: // data
                    var data = new byte[count];
                    Array.Copy(bytes, 4, data, 0, count);
                    var abs = (upper << 16) | offset;
                    records.Add((abs, data));
                    if (abs < min) min = abs;
                    if (abs + (uint)count > max) max = abs + (uint)count;
                    break;
                case 0x01: // EOF
                    return Flatten(records, min, max);
                case 0x04: // extended linear address
                    if (count != 2) throw new InvalidDataException($"Intel HEX line {lineNo} ELA record must be 2 bytes.");
                    upper = (uint)((bytes[4] << 8) | bytes[5]);
                    break;
                case 0x05: // start linear address — execution entry, irrelevant to flashing
                    break;
                default:
                    throw new InvalidDataException($"Intel HEX line {lineNo} has unsupported record type 0x{type:X2}.");
            }
        }

        // No EOF record — still flatten what we have rather than throw.
        return Flatten(records, min, max);
    }

    private static IntelHexImage Flatten(
        System.Collections.Generic.List<(uint addr, byte[] data)> records, uint min, uint max)
    {
        if (records.Count == 0) throw new InvalidDataException("Intel HEX contained no data records.");

        var flat = new byte[max - min];
        Array.Fill(flat, (byte)0xFF); // erased-flash gaps
        foreach (var (addr, data) in records)
            Array.Copy(data, 0, flat, addr - min, data.Length);

        return new IntelHexImage { BaseAddress = min, Data = flat };
    }

    private static byte[] HexToBytes(ReadOnlySpan<char> chars, int lineNo)
    {
        if ((chars.Length & 1) != 0) throw new InvalidDataException($"Intel HEX line {lineNo} has odd hex length.");
        var result = new byte[chars.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(chars.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                throw new InvalidDataException($"Intel HEX line {lineNo} has non-hex characters.");
        }
        return result;
    }
}
