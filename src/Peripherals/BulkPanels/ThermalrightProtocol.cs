using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Thermalright's LCD coolers. One 64-byte header then the frame, both in a SINGLE bulk
/// transfer - unlike the Kraken, where concatenating the header with the pixels corrupts
/// the upload. Reconstructed from third-party documentation; no unit has been run against it.
///
/// The panel identifies itself: an init packet answered on the bulk IN pipe carries a
/// model byte, and the model decides the resolution and whether the frame is JPEG or
/// RGB565. That is why this is a family rather than a fixed size.
/// </summary>
public static class ThermalrightProtocol
{
    public const int HeaderLength = 64;

    /// <summary>Every packet in either direction opens with these four bytes.</summary>
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x12, 0x34, 0x56, 0x78 };

    private const byte CommandInit = 0x00;
    private const byte CommandJpeg = 0x02;
    private const byte CommandRgb565 = 0x03;

    /// <summary>Marks the packet kind at byte 56: 1 for the init request, 2 for a frame.</summary>
    private const int KindOffset = 56;

    /// <summary>Bytes 4-7 of a reply read A1 A2 A3 A4 while the panel is still booting.</summary>
    public static bool IsBooting(ReadOnlySpan<byte> reply) =>
        reply.Length >= 8 && reply[4] == 0xA1 && reply[5] == 0xA2 && reply[6] == 0xA3 && reply[7] == 0xA4;

    public static bool HasMagic(ReadOnlySpan<byte> packet) =>
        packet.Length >= 4 && packet[0] == 0x12 && packet[1] == 0x34 && packet[2] == 0x56 && packet[3] == 0x78;

    public static byte[] EncodeInitRequest()
    {
        var packet = new byte[HeaderLength];
        Magic.CopyTo(packet);
        packet[4] = CommandInit;
        packet[KindOffset] = 0x01;
        return packet;
    }

    /// <summary>
    /// Primary model id from an init reply, or null when the reply is not usable yet.
    /// Byte 24 selects the panel; byte 28 is a sub-model that only disambiguates marketing
    /// names, so it is not read here.
    /// </summary>
    public static byte? DecodeModelId(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 29 || IsBooting(reply) || !HasMagic(reply))
        {
            return null;
        }
        return reply[24];
    }

    /// <summary>
    /// Builds the header for one frame. The length field is the payload only; the caller
    /// sends header and payload as one transfer.
    /// </summary>
    public static byte[] EncodeFrameHeader(int width, int height, int payloadLength, bool rgb565)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = rgb565 ? CommandRgb565 : CommandJpeg;
        header[8] = (byte)(width & 0xFF);
        header[9] = (byte)((width >> 8) & 0xFF);
        header[12] = (byte)(height & 0xFF);
        header[13] = (byte)((height >> 8) & 0xFF);
        header[KindOffset] = 0x02;
        header[60] = (byte)(payloadLength & 0xFF);
        header[61] = (byte)((payloadLength >> 8) & 0xFF);
        header[62] = (byte)((payloadLength >> 16) & 0xFF);
        header[63] = (byte)((payloadLength >> 24) & 0xFF);
        return header;
    }

    /// <summary>
    /// Panels this driver knows, keyed by the model byte the init reply carries. Sizes are
    /// the resolution the FIRMWARE wants, which is not always the physical panel: the
    /// Wonder Vision is a 2400x1080 screen that upscales from 1600x720.
    /// </summary>
    public static ThermalrightPanel? PanelFor(byte modelId) => modelId switch
    {
        0x01 => new ThermalrightPanel("Grand Vision", 480, 480, false),
        0x03 => new ThermalrightPanel("Core Vision", 480, 480, false),
        0x04 => new ThermalrightPanel("Hyper Vision", 480, 480, false),
        0x05 => new ThermalrightPanel("Mjolnir Vision", 640, 480, false),
        0x06 => new ThermalrightPanel("Frozen Vision", 640, 480, false),
        0x07 => new ThermalrightPanel("Stream Vision", 640, 480, false),
        0x0B => new ThermalrightPanel("Vision Max", 854, 480, false),
        // The only RGB565 member, and the only one that answers no init at all - the
        // handshake times out on it, which is how it is identified.
        0x20 => new ThermalrightPanel("Frozen Warframe Pro", 320, 320, true),
        0x40 => new ThermalrightPanel("Wonder Vision", 1600, 720, false),
        0x41 or 0x42 => new ThermalrightPanel("TL-M10 Vision", 1920, 462, false),
        _ => null,
    };

    /// <summary>The model to assume when the init handshake never answers.</summary>
    public const byte FallbackModelId = 0x20;
}

/// <summary>One Thermalright panel: the size the firmware expects and how it wants pixels.</summary>
public readonly record struct ThermalrightPanel(string Name, int Width, int Height, bool Rgb565);
