using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Pure builders/parsers for the Elgato Stream Deck gen1 wire protocol (Mini,
/// Mini MK.2, Mini Discord, Mini MK.2 Module, and the legacy Original). No IO;
/// <see cref="HidStreamDeckSurface"/> owns the HID handle and performs the
/// feature-report + output-report exchanges. Gen2 (JPEG-based, report id
/// 0x02/0x03 with an 8-byte image header) is Phase 3.
///
/// Byte layouts are transcribed from two independent MIT references (fetched
/// 2026-07-10, cited per function): python-elgato-streamdeck
/// (StreamDeckMini.py, StreamDeckOriginal.py) and the elgato-streamdeck Rust
/// crate (src/lib.rs, src/util.rs). Feature-report buffers are sized to
/// FeatureReportBufferLength (32), the bench-confirmed HidP_GetCaps value for
/// the Mini - larger than the references' own 17-byte buffers, since
/// HidD_SetFeature on Windows requires a buffer at least as long as the
/// device's declared FeatureReportByteLength.
/// </summary>
public static class StreamDeckProtocol
{
    /// <summary>Bench-confirmed (T1 Mini, 2026-07-10) HidP_GetCaps FeatureReportByteLength.</summary>
    public const int FeatureReportBufferLength = 32;

    /// <summary>Gen1 image-report page header length (python-elgato-streamdeck IMAGE_REPORT_HEADER_LENGTH).</summary>
    public const int PageHeaderLength = 16;

    private const byte ResetReportId = 0x0B;
    private const byte ResetCommand = 0x63;

    private const byte BrightnessReportId = 0x05;
    private const byte BrightnessMagic1 = 0x55;
    private const byte BrightnessMagic2 = 0xAA;
    private const byte BrightnessMagic3 = 0xD1;
    private const byte BrightnessMagic4 = 0x01;

    private const byte ImageReportId = 0x02;
    private const byte ImageReportMarker = 0x01;

    private const byte SerialFeatureReportId = 0x03;
    private const byte FirmwareFeatureReportId = 0x04;

    /// <summary>ASCII string payloads (serial, firmware) start at this offset in the feature response.</summary>
    private const int StringPayloadOffset = 5;

    /// <summary>
    /// 0x0B 0x63 reset command (python StreamDeckMini.reset / StreamDeckOriginal.reset;
    /// Rust lib.rs reset(), Original/Mini match arm).
    /// </summary>
    public static byte[] BuildResetFeature(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = ResetReportId;
        buf[1] = ResetCommand;
        return buf;
    }

    /// <summary>
    /// 0x05 0x55 0xAA 0xD1 0x01 &lt;pct&gt; brightness command, pct clamped 0-100
    /// (python StreamDeckMini.set_brightness; Rust lib.rs set_brightness(),
    /// Original/Mini match arm).
    /// </summary>
    public static byte[] BuildBrightnessFeature(int percent, int featureReportLength = FeatureReportBufferLength)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var buf = new byte[featureReportLength];
        buf[0] = BrightnessReportId;
        buf[1] = BrightnessMagic1;
        buf[2] = BrightnessMagic2;
        buf[3] = BrightnessMagic3;
        buf[4] = BrightnessMagic4;
        buf[5] = (byte)clamped;
        return buf;
    }

    /// <summary>Feature request priming a serial-number read-back (report id 0x03).</summary>
    public static byte[] BuildSerialFeatureRequest(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = SerialFeatureReportId;
        return buf;
    }

    /// <summary>Feature request priming a firmware-version read-back (report id 0x04).</summary>
    public static byte[] BuildFirmwareFeatureRequest(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = FirmwareFeatureReportId;
        return buf;
    }

    /// <summary>
    /// Extracts the null-terminated ASCII string from a serial/firmware feature
    /// response (python _extract_string(bytes[5:]); Rust util::extract_str).
    /// </summary>
    public static string ExtractAsciiString(ReadOnlySpan<byte> featureResponse)
    {
        if (featureResponse.Length <= StringPayloadOffset)
        {
            return "";
        }
        var payload = featureResponse[StringPayloadOffset..];
        var end = payload.IndexOf((byte)0);
        var text = end >= 0 ? payload[..end] : payload;
        return System.Text.Encoding.ASCII.GetString(text);
    }

    /// <summary>
    /// Splits already-encoded wire bytes (a BMP for gen1) into
    /// model.ImageReportLength-sized pages carrying the 16-byte gen1 header
    /// (report id 0x02, marker 0x01, page number, always-zero byte, is-last
    /// flag, 1-based raw key index, 10 zero padding bytes), then zero-pads the
    /// final page to the full report length. rawKeyIndex is the hardware key
    /// index (call model.RemapKeyIndex first). Chunking matches
    /// python-elgato-streamdeck StreamDeckMini.set_key_image /
    /// StreamDeckOriginal.set_key_image and Rust lib.rs send_image /
    /// write_image_data_reports; the Original halves the payload across
    /// exactly 2 pages and numbers them from 1 instead of 0 - a real firmware
    /// quirk, not a guess.
    /// </summary>
    public static List<byte[]> BuildImagePages(ReadOnlySpan<byte> wireBytes, int rawKeyIndex, StreamDeckModel model)
    {
        var payloadLength = model.HalvedImagePayload
            ? wireBytes.Length / 2
            : model.ImageReportLength - PageHeaderLength;

        var pages = new List<byte[]>();
        var offset = 0;
        var remaining = wireBytes.Length;
        var pageNumber = 0;
        while (remaining > 0)
        {
            var thisLength = Math.Min(remaining, payloadLength);
            var isLast = thisLength == remaining;

            var page = new byte[model.ImageReportLength];
            page[0] = ImageReportId;
            page[1] = ImageReportMarker;
            page[2] = (byte)(pageNumber + model.ImagePageNumberBase);
            page[3] = 0;
            page[4] = (byte)(isLast ? 1 : 0);
            page[5] = (byte)(rawKeyIndex + 1);
            wireBytes.Slice(offset, thisLength).CopyTo(page.AsSpan(PageHeaderLength, thisLength));
            pages.Add(page);

            offset += thisLength;
            remaining -= thisLength;
            pageNumber++;
        }
        return pages;
    }

    /// <summary>
    /// Decodes a gen1 input report: byte 0 is the report id, one byte per key
    /// from offset 1 (nonzero = pressed), in the model's raw hardware key
    /// order (python StreamDeckMini._read_control_states; Rust util::
    /// read_button_states, Mini/MiniMk2/MiniDiscord/MiniMk2Module match arm).
    /// Applies the model's key-index remap to return canonical order.
    /// </summary>
    public static bool[] DecodeGen1Input(ReadOnlySpan<byte> report, StreamDeckModel model)
    {
        var raw = new bool[model.KeyCount];
        for (var i = 0; i < model.KeyCount; i++)
        {
            var offset = i + 1;
            raw[i] = offset < report.Length && report[offset] != 0;
        }

        if (!model.KeyIndexRightToLeft)
        {
            return raw;
        }

        var canonical = new bool[model.KeyCount];
        for (var k = 0; k < model.KeyCount; k++)
        {
            canonical[k] = raw[model.RemapKeyIndex(k)];
        }
        return canonical;
    }

    /// <summary>
    /// A fixed, correctly-sized all-black 24bpp BMP for a square key of the
    /// given pixel size, used only by <see cref="HidStreamDeckSurface.ClearKey"/>.
    /// Not a general encoder: no color/resizing parameters, no decode path -
    /// just the standard 54-byte BITMAPFILEHEADER+BITMAPINFOHEADER (bottom-up
    /// row order, so an all-zero body is a valid solid-black image regardless
    /// of row order) followed by a zero-filled pixel buffer.
    /// </summary>
    public static byte[] BuildBlankBmp(int size)
    {
        var pixelBytes = size * size * 3;
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54); // pixel data offset
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40); // DIB header size
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), size);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), size);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24); // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), (uint)pixelBytes);
        // Pixel bytes 54.. stay zero (black), rows already bottom-up-identical for a solid fill.
        return bmp;
    }
}
