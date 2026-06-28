using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Byte-level constants and pure parsers for the Corsair iCUE LINK System Hub
/// (USB 1B1C:0C3F) HID protocol. No IO; <see cref="CorsairLinkHub"/> owns the
/// transport and the endpoint paging. Protocol cross-validated against
/// OpenLinkHub (src/devices/lsh/lsh.go), OpenRGB (CorsairICueLinkController),
/// and confirmed live on firmware 3.2.571.
///
/// Frame: a 513-byte write buffer [reportId=0x00, 0x00, 0x01, command..., payload...].
/// The command starts at offset <see cref="HeaderSize"/>=3. Every write is
/// answered by one 512-byte interrupt-IN report. Reads address an "endpoint"
/// (a mode byte) and must close -> open -> read -> close it.
/// Wire color order is R,G,B (no swap).
/// </summary>
public static class CorsairLinkProtocol
{
    public const int VendorId = 0x1B1C;
    public const int ProductId = 0x0C3F;

    /// <summary>Command interface (MI_00). The MI_01 sibling carries usage 0x02 and is not used.</summary>
    public const int VendorUsagePage = 0xFF42;
    public const int VendorUsage = 0x01;

    /// <summary>On-wire report length; the write buffer is this + 1 report-id byte.</summary>
    public const int ReportLength = 512;
    public const int WriteBufferLength = ReportLength + 1;

    /// <summary>Bytes before the command: {reportId=0x00, 0x00, 0x01}. Command starts here.</summary>
    public const int HeaderSize = 3;

    /// <summary>Max color bytes per chunk before the 3-byte header + 2-byte command push past 513.</summary>
    public const int MaxColorChunk = 508;

    /// <summary>Firmware 2.3.427+ enumerates up to 24 daisy-chain channels; index 0 is reserved.</summary>
    public const int MaxChannels = 24;

    /// <summary>Speed/temperature sensor arrays are 1-based by channel; size for index 0..MaxChannels.</summary>
    public const int SensorArrayLength = MaxChannels + 1;

    // Commands (placed at write-buffer offset 3). ReadOnlySpan<byte> over a
    // constant array compiles to a static RVA blob (no per-call allocation).
    public static ReadOnlySpan<byte> CmdGetFirmware => new byte[] { 0x02, 0x13 };
    public static ReadOnlySpan<byte> CmdSoftwareMode => new byte[] { 0x01, 0x03, 0x00, 0x02 };
    public static ReadOnlySpan<byte> CmdHardwareMode => new byte[] { 0x01, 0x03, 0x00, 0x01 };
    public static ReadOnlySpan<byte> CmdOpenEndpoint => new byte[] { 0x0D, 0x01 };
    public static ReadOnlySpan<byte> CmdOpenColorEndpoint => new byte[] { 0x0D, 0x00 };
    public static ReadOnlySpan<byte> CmdCloseEndpoint => new byte[] { 0x05, 0x01, 0x01 };
    public static ReadOnlySpan<byte> CmdWrite => new byte[] { 0x06, 0x01 };
    public static ReadOnlySpan<byte> CmdWriteColor => new byte[] { 0x06, 0x00 };
    public static ReadOnlySpan<byte> CmdWriteSubColor => new byte[] { 0x07, 0x00 };
    public static ReadOnlySpan<byte> CmdRead => new byte[] { 0x08, 0x01 };

    // Endpoint (mode) addresses, passed as the payload of close/open/read.
    public const byte ModeGetDevices = 0x36;
    public const byte ModeGetTemperatures = 0x21;
    public const byte ModeGetSpeeds = 0x17;
    public const byte ModeSetSpeed = 0x18;
    public const byte ModeSetColor = 0x22;

    // Data-type tags echoed at response[4:6]. A read must match the expected tag
    // there or it is a stale queued report from a prior command; re-read to resync.
    public static ReadOnlySpan<byte> DataGetDevices => new byte[] { 0x21, 0x00 };
    public static ReadOnlySpan<byte> DataGetTemperatures => new byte[] { 0x10, 0x00 };
    public static ReadOnlySpan<byte> DataGetSpeeds => new byte[] { 0x25, 0x00 };
    public static ReadOnlySpan<byte> DataSetSpeed => new byte[] { 0x07, 0x00 };
    public static ReadOnlySpan<byte> DataSetColor => new byte[] { 0x12, 0x00 };

    /// <summary>Re-reads to drain stale queued responses until the data-type matches.</summary>
    public const int ReadResyncTries = 5;

    /// <summary>Firmware needs this settle after entering software mode before any other command.</summary>
    public const int SoftwareModeSettleMs = 500;

    /// <summary>A daisy-chained device discovered by <see cref="ParseDevices"/>.</summary>
    public readonly struct DiscoveredDevice
    {
        public DiscoveredDevice(int channel, int type, int model, string serial)
        {
            Channel = channel;
            Type = type;
            Model = model;
            Serial = serial;
        }

        /// <summary>1-based daisy-chain position (the LED/speed addressing index).</summary>
        public int Channel { get; }
        public int Type { get; }
        public int Model { get; }
        public string Serial { get; }
    }

    /// <summary>
    /// Decode the connected-device list from a <see cref="ModeGetDevices"/> read.
    /// channels=resp[6]; each slot is an 8-byte record (type=rec[2], model=rec[3])
    /// followed by a variable-length serial whose length is rec[7]. A slot with
    /// length 0 is empty.
    /// </summary>
    public static List<DiscoveredDevice> ParseDevices(ReadOnlySpan<byte> resp)
    {
        var list = new List<DiscoveredDevice>();
        if (resp.Length < 7) return list;
        int channels = resp[6];
        var data = resp.Slice(7);
        var pos = 0;
        for (var i = 1; i <= channels; i++)
        {
            if (pos + 8 > data.Length) break;
            int idLen = data[pos + 7];
            if (idLen == 0)
            {
                pos += 8;
                continue;
            }
            int type = data[pos + 2];
            int model = data[pos + 3];
            var serial = "";
            if (pos + 8 + idLen <= data.Length)
            {
                serial = System.Text.Encoding.ASCII.GetString(data.Slice(pos + 8, idLen));
            }
            list.Add(new DiscoveredDevice(i, type, model, serial));
            pos += 8 + idLen;
        }
        return list;
    }

    /// <summary>
    /// Fill <paramref name="rpmByChannel"/> from a <see cref="ModeGetSpeeds"/> read.
    /// amount=resp[6]; each sensor is 3 bytes [status, lo, hi]; status 0 = valid.
    /// Sensor index equals the device channel (index 0 is the reserved hub slot).
    /// </summary>
    public static void ParseSpeeds(ReadOnlySpan<byte> resp, int[] rpmByChannel)
    {
        if (resp.Length < 7) return;
        int amount = resp[6];
        var data = resp.Slice(7);
        for (var i = 0; i < amount && i < rpmByChannel.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= data.Length) break;
            var status = data[off];
            var val = data[off + 1] | (data[off + 2] << 8);
            rpmByChannel[i] = status == 0 ? val : -1;
        }
    }

    /// <summary>
    /// Fill <paramref name="tempByChannel"/> (tenths-of-degree decoded to Celsius)
    /// from a <see cref="ModeGetTemperatures"/> read. status 0 = valid; invalid
    /// sensors are set to NaN.
    /// </summary>
    public static void ParseTemperatures(ReadOnlySpan<byte> resp, float[] tempByChannel)
    {
        if (resp.Length < 7) return;
        int amount = resp[6];
        var data = resp.Slice(7);
        for (var i = 0; i < amount && i < tempByChannel.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= data.Length) break;
            var status = data[off];
            var raw = (short)(data[off + 1] | (data[off + 2] << 8));
            tempByChannel[i] = status == 0 ? raw / 10f : float.NaN;
        }
    }
}
