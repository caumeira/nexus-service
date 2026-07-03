using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Pure C# port of the TinyUZ wire format (github.com/sisong/tinyuz) as used
/// by the SLV3 firmware's RF_RgbSync decoder (plans/lianli-wireless-support.md
/// section 2): a 4-byte little-endian dictionary-size header followed by a
/// type-bit stream where each bit selects a literal data byte or a control
/// code. This encoder emits literals only (no back-references), matching
/// phstudy/uni-wireless-sync's compress_led_payload - one of the two
/// byte-identical, hardware-tested encoders the plan cites for this exact
/// firmware. A literal-only stream is always valid TinyUZ input; it just
/// forgoes the ratio a real LZ matcher would add, which single-frame LED
/// pushes (well under <see cref="MaxCompressedLength"/>) do not need.
/// </summary>
public static class TinyUz
{
    /// <summary>Dictionary window advertised in the stream header. 4096 (12-bit); a larger window crashes the firmware.</summary>
    public const int DictSize = 4096;

    /// <summary>lzo_rgb_rf_valid_len cap; the firmware throws "out of max uz length" past this.</summary>
    public const int MaxCompressedLength = 12288;

    private const int CtrlStreamEnd = 3;
    private const int CodeTypeDict = 0;
    private const int CodeTypeData = 1;

    /// <summary>
    /// Encodes <paramref name="data"/> as a literal-only TinyUZ stream. Throws
    /// if <paramref name="data"/> is empty or the encoded output exceeds
    /// <see cref="MaxCompressedLength"/>.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new ArgumentException("data cannot be empty", nameof(data));
        }

        var code = new List<byte>(data.Length + data.Length / 8 + 8);
        for (var shift = 0; shift < 4; shift++)
        {
            code.Add((byte)((DictSize >> (8 * shift)) & 0xFF));
        }

        var typeCount = 0;
        var typesIndex = -1;

        void OutType(int bit)
        {
            if (typeCount == 0)
            {
                typesIndex = code.Count;
                code.Add(0);
            }
            code[typesIndex] = (byte)(code[typesIndex] | ((bit & 1) << typeCount));
            typeCount = (typeCount + 1) % 8;
            if (typeCount == 0)
            {
                typesIndex = -1;
            }
        }

        // Elias-gamma-like length code: decompose value into (count, remainder)
        // by repeatedly subtracting the next power-of-(1<<packBit) threshold,
        // then emit each chunk MSB-first with a continuation bit.
        void OutLen(int value, int packBit)
        {
            var count = 1;
            var v = value;
            while (v >= (1 << (count * packBit)))
            {
                v -= 1 << (count * packBit);
                count++;
            }
            for (var idx = count - 1; idx >= 0; idx--)
            {
                for (var bitIndex = 0; bitIndex < packBit; bitIndex++)
                {
                    var shift = idx * packBit + bitIndex;
                    OutType((v >> shift) & 1);
                }
                OutType(idx > 0 ? 1 : 0);
            }
        }

        foreach (var b in data)
        {
            OutType(CodeTypeData);
            code.Add(b);
        }

        // Stream-end control code: dict_pos=0 carries no back-reference, and
        // the reuse flag after the length code is 0 because the last emission
        // above was always a literal (data.Length > 0).
        OutType(CodeTypeDict);
        OutLen(CtrlStreamEnd, 1);
        OutType(0);
        code.Add(0);

        var result = code.ToArray();
        if (result.Length > MaxCompressedLength)
        {
            throw new InvalidOperationException("out of max uz length");
        }
        return result;
    }

    /// <summary>
    /// Decodes a stream produced by <see cref="Compress"/> (literal-only,
    /// single stream-end control code). Used by tests to verify the round
    /// trip; the service never receives TinyUZ data back from the firmware.
    /// </summary>
    internal static (int DictSize, byte[] Data) Decompress(ReadOnlySpan<byte> encodedSpan)
    {
        // Local functions below share mutable state across closures, which the
        // compiler cannot do over a ref-like Span; copy to an array first.
        var encoded = encodedSpan.ToArray();
        var dictSize = encoded[0] | (encoded[1] << 8) | (encoded[2] << 16) | (encoded[3] << 24);
        var pos = 4;
        var typeBits = 0;
        var bitsLeft = 0;

        byte ReadByte() => encoded[pos++];

        int ReadTypeBit()
        {
            if (bitsLeft == 0)
            {
                typeBits = ReadByte();
                bitsLeft = 8;
            }
            var bit = typeBits & 1;
            typeBits >>= 1;
            bitsLeft--;
            return bit;
        }

        int ReadLen(int packBit)
        {
            var value = 0;
            while (true)
            {
                var low = 0;
                for (var i = 0; i < packBit; i++)
                {
                    low |= ReadTypeBit() << i;
                }
                var flag = ReadTypeBit();
                value = (value << packBit) + low;
                if (flag == 0)
                {
                    return value;
                }
                value += 1;
            }
        }

        var output = new List<byte>();
        while (true)
        {
            var codeType = ReadTypeBit();
            if (codeType == CodeTypeData)
            {
                output.Add(ReadByte());
                continue;
            }

            var savedLen = ReadLen(1);
            if (output.Count > 0)
            {
                ReadTypeBit(); // reuse flag; the literal-only encoder always writes 0 here.
            }
            ReadByte(); // dict_pos; always 0 for a literal-only stream.
            if (savedLen == CtrlStreamEnd)
            {
                break;
            }
            throw new NotSupportedException($"unsupported TinyUZ control code {savedLen}");
        }
        return (dictSize, output.ToArray());
    }
}
