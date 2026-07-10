using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Pure builders/parsers for the Elgato Stream Deck wire protocol, both
/// generations. No IO; <see cref="HidStreamDeckSurface"/> owns the HID handle
/// and performs the feature-report + output-report exchanges.
///
/// Byte layouts are transcribed from two independent MIT references (gen1
/// fetched 2026-07-10, gen2 fetched 2026-07-10): python-elgato-streamdeck
/// (StreamDeck/Devices/StreamDeckMini.py, StreamDeckOriginal.py,
/// StreamDeckOriginalV2.py, StreamDeckXL.py, StreamDeckNeo.py,
/// StreamDeckPedal.py) and the elgato-streamdeck Rust crate
/// (github.com/OpenActionAPI/rust-elgato-streamdeck: src/lib.rs, src/util.rs,
/// src/info.rs). Feature-report buffers are sized to FeatureReportBufferLength
/// (32), the bench-confirmed HidP_GetCaps value for the Mini - larger than
/// the references' own 17-byte buffers, since HidD_SetFeature on Windows
/// requires a buffer at least as long as the device's declared
/// FeatureReportByteLength. Only the Mini is bench-verified; every gen2 model
/// (Original V2, MK.2 family, XL family, Neo, Pedal) stays Verified=false
/// until a unit passes a real bench checklist.
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

    /// <summary>ASCII string payloads (serial, firmware) start at this offset in the gen1 feature response.</summary>
    private const int StringPayloadOffset = 5;

    // ── Gen2 (Original V2, MK.2 family, XL family, Neo, Pedal) ──
    // Cross-verified byte-for-byte between both references for every gen2
    // model: python-elgato-streamdeck's StreamDeckOriginalV2/XL/Neo/Pedal
    // set_brightness/reset/get_serial_number/get_firmware_version/
    // set_key_image/_read_control_states, and the Rust crate's
    // lib.rs reset()/set_brightness()/serial_number()/firmware_version()/
    // send_image() catch-all (non-gen1) match arms plus util.rs
    // read_button_states's catch-all arm. One disagreement found: python's
    // set_brightness builds its payload via bytearray slice assignment
    // (`payload[0:2] = [0x03, 0x08, percent]`), which resizes a 32-byte
    // buffer to 33 bytes (a 3-element list replacing a 2-element slice) -
    // almost certainly an unintended artifact of that assignment form, not a
    // verified different wire length. The Rust crate builds the same command
    // as a clean 32-byte buffer (3 explicit bytes + 29 zero bytes), matching
    // this codebase's existing FeatureReportBufferLength convention, so gen2
    // brightness/reset here use 32 bytes like gen1's Mini family.
    private const byte Gen2FeatureReportId = 0x03;
    private const byte Gen2ResetCommand = 0x02;
    private const byte Gen2BrightnessCommand = 0x08;

    private const byte Gen2SerialFeatureReportId = 0x06;
    private const byte Gen2FirmwareFeatureReportId = 0x05;

    /// <summary>Gen2 serial-number ASCII payload offset (python get_serial_number: serial[2:]; Rust serial_number()'s non-Mini-family arm: bytes[2..]).</summary>
    public const int Gen2SerialStringOffset = 2;
    /// <summary>Gen2 firmware-version ASCII payload offset (python get_firmware_version: version[6:]; Rust firmware_version()'s non-Mini-family arm: bytes[6..]).</summary>
    public const int Gen2FirmwareStringOffset = 6;

    private const byte Gen2ImageReportId = 0x02;
    private const byte Gen2ImageReportSubtype = 0x07;

    /// <summary>Gen2 image-report page header length (python IMAGE_REPORT_HEADER_LENGTH; Rust WriteImageParameters::for_key's non-gen1 arm).</summary>
    public const int Gen2PageHeaderLength = 8;

    /// <summary>Gen2 input report header length before key states (python `states[4:]`; Rust util::read_button_states's catch-all arm: `states[4..]`).</summary>
    public const int Gen2InputHeaderLength = 4;

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
    /// response (python _extract_string(bytes[offset:]); Rust util::extract_str).
    /// Gen1 uses the same offset (5) for both serial and firmware; gen2 uses
    /// different offsets per purpose (<see cref="Gen2SerialStringOffset"/>,
    /// <see cref="Gen2FirmwareStringOffset"/>), passed explicitly by the caller.
    /// </summary>
    public static string ExtractAsciiString(ReadOnlySpan<byte> featureResponse, int offset = StringPayloadOffset)
    {
        if (featureResponse.Length <= offset)
        {
            return "";
        }
        var payload = featureResponse[offset..];
        var end = payload.IndexOf((byte)0);
        var text = end >= 0 ? payload[..end] : payload;
        return System.Text.Encoding.ASCII.GetString(text);
    }

    /// <summary>0x03 0x02 reset command, 32 bytes total (python StreamDeckOriginalV2/XL/Neo.reset; Rust lib.rs reset(), non-gen1 arm).</summary>
    public static byte[] BuildGen2ResetFeature(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = Gen2FeatureReportId;
        buf[1] = Gen2ResetCommand;
        return buf;
    }

    /// <summary>0x03 0x08 &lt;pct&gt; brightness command, pct clamped 0-100, 32 bytes total (python StreamDeckOriginalV2/XL/Neo.set_brightness; Rust lib.rs set_brightness(), non-gen1 arm).</summary>
    public static byte[] BuildGen2BrightnessFeature(int percent, int featureReportLength = FeatureReportBufferLength)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var buf = new byte[featureReportLength];
        buf[0] = Gen2FeatureReportId;
        buf[1] = Gen2BrightnessCommand;
        buf[2] = (byte)clamped;
        return buf;
    }

    /// <summary>Feature request priming a gen2 serial-number read-back (report id 0x06).</summary>
    public static byte[] BuildGen2SerialFeatureRequest(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = Gen2SerialFeatureReportId;
        return buf;
    }

    /// <summary>Feature request priming a gen2 firmware-version read-back (report id 0x05).</summary>
    public static byte[] BuildGen2FirmwareFeatureRequest(int featureReportLength = FeatureReportBufferLength)
    {
        var buf = new byte[featureReportLength];
        buf[0] = Gen2FirmwareFeatureReportId;
        return buf;
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
        // Ceiling division so an odd wireBytes.Length still splits into
        // exactly 2 pages (a plain / 2 would leave 1 byte for a 3rd page).
        var payloadLength = model.HalvedImagePayload
            ? (wireBytes.Length + 1) / 2
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
    /// Splits already-encoded wire bytes (a JPEG for gen2) into
    /// model.ImageReportLength-sized pages carrying the 8-byte gen2 header
    /// (report id 0x02, subtype 0x07, raw key index (0-based, no +1), is-last
    /// flag, little-endian payload length, little-endian page number), then
    /// zero-pads the final page to the full report length. rawKeyIndex is the
    /// hardware key index (call model.RemapKeyIndex first, though no gen2
    /// model remaps). Chunking matches python-elgato-streamdeck
    /// StreamDeckOriginalV2/XL/Neo.set_key_image and Rust lib.rs send_image's
    /// non-gen1 header closure / WriteImageParameters::for_key's non-gen1 arm
    /// (payload length = ImageReportLength - 8, no halving).
    /// </summary>
    public static List<byte[]> BuildGen2ImagePages(ReadOnlySpan<byte> wireBytes, int rawKeyIndex, StreamDeckModel model)
    {
        var payloadLength = model.ImageReportLength - Gen2PageHeaderLength;

        var pages = new List<byte[]>();
        var offset = 0;
        var remaining = wireBytes.Length;
        var pageNumber = 0;
        while (remaining > 0)
        {
            var thisLength = Math.Min(remaining, payloadLength);
            var isLast = thisLength == remaining;

            var page = new byte[model.ImageReportLength];
            page[0] = Gen2ImageReportId;
            page[1] = Gen2ImageReportSubtype;
            page[2] = (byte)rawKeyIndex;
            page[3] = (byte)(isLast ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(4), (ushort)thisLength);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(6), (ushort)pageNumber);
            wireBytes.Slice(offset, thisLength).CopyTo(page.AsSpan(Gen2PageHeaderLength, thisLength));
            pages.Add(page);

            offset += thisLength;
            remaining -= thisLength;
            pageNumber++;
        }
        return pages;
    }

    /// <summary>
    /// Decodes a gen2 input report: a 4-byte header, then one byte per key
    /// from offset 4 (nonzero = pressed), in hardware order (python
    /// StreamDeckOriginalV2/XL/Neo/Pedal._read_control_states: `states[4:]`;
    /// Rust util::read_button_states catch-all arm: `states[4..]`). No gen2
    /// model remaps key order. Neo's 2 capacitive touch keys land at report
    /// offsets model.KeyCount and model.KeyCount+1 per both references, but
    /// are not decoded here - see StreamDeckModels.cs for why they are not
    /// wired as bindable keys in this pass.
    /// </summary>
    public static bool[] DecodeGen2Input(ReadOnlySpan<byte> report, StreamDeckModel model)
    {
        var states = new bool[model.KeyCount];
        for (var i = 0; i < model.KeyCount; i++)
        {
            var offset = Gen2InputHeaderLength + i;
            states[i] = offset < report.Length && report[offset] != 0;
        }
        return states;
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
