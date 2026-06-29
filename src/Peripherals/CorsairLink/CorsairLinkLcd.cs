using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Controls the LCD HID interface (AIO pump LCD or XD5 Elite LCD). This is a
/// separate USB interface from the iCUE LINK hub: VID 0x1B1C, PID 0x0C4E (AIO)
/// or 0x0C43 (XD5), OutputReportByteLength 1024. The LCD uses numbered HID report
/// id 0x02 (buffer[0]); the 1024-byte write buffer is byte-identical to OpenLinkHub
/// transferToLcd. All public operations run under <see cref="_lock"/>.
/// </summary>
public sealed class CorsairLinkLcd
{
    // lsh.go lcdBufferSize / lcdHeaderSize.
    internal const int LcdBufferSize = 1024;
    internal const int LcdHeaderSize = 8;
    internal const int MaxPayloadPerChunk = LcdBufferSize - LcdHeaderSize; // 1016
    // lcd.go product ids (decimal 3150 / 3139).
    internal const int AioPid = 0x0C4E;
    internal const int Xd5Pid = 0x0C43;
    internal const int LcdVendorId = 0x1B1C;
    // The LCD's HID output report incl. the report-id byte at [0]; matches OLH lcdBufferSize.
    internal const int LcdOutputReportByteLength = LcdBufferSize;

    private readonly object _lock = new();
    // 1024-byte HID output report: [0]=0x02 report id, header [1..7], payload [8..].
    // Allocated once and reused under _lock.
    private readonly byte[] _writeBuffer = new byte[LcdOutputReportByteLength];
    private IHidDevice? _device;
    private volatile bool _attached;

    /// <summary>True when a usable LCD HID device is open.</summary>
    public bool HasDevice => _attached;

    /// <summary>
    /// Scans <paramref name="chain"/> for an LCD chain device (type 6 or 14), then opens the
    /// matching HID interface by VID, PID, serial, and OutputReportByteLength. OLH init does
    /// not send a power-on report (lsh.go:452-458); the worker applies rotation + brightness.
    /// </summary>
    public void DiscoverAndAttach(IReadOnlyList<CorsairLinkDevice> chain, IHidEnumerator hid)
    {
        lock (_lock)
        {
            CorsairLinkDevice? lcdDev = null;
            foreach (var d in chain)
            {
                if (d.Type == 6 || d.Type == 14)
                {
                    lcdDev = d;
                    break;
                }
            }
            if (lcdDev is null) return;

            var opened = TryOpenMatchingHid(hid, AioPid, lcdDev.Serial)
                ?? TryOpenMatchingHid(hid, Xd5Pid, lcdDev.Serial);
            if (opened is null) return;

            _device = opened;
            _attached = true;
        }
    }

    private static IHidDevice? TryOpenMatchingHid(IHidEnumerator hid, int pid, string serial)
    {
        foreach (var info in hid.Find(LcdVendorId, pid))
        {
            if (info.OutputReportByteLength != LcdOutputReportByteLength) continue;
            if (info.Serial != serial) continue;
            var dev = hid.Open(info.Path, forInput: false);
            if (dev is not null) return dev;
        }
        return null;
    }

    /// <summary>Sends the shutdown feature-report sequence and disposes the device.</summary>
    public void Detach()
    {
        lock (_lock)
        {
            if (_device is null) return;
            SendShutdown();
            _device.Dispose();
            _device = null;
            _attached = false;
        }
    }

    /// <summary>
    /// Splits <paramref name="jpeg"/> into chunks and writes each to the LCD.
    /// The firmware renders (latches) when it receives a chunk shorter than 1016 bytes.
    /// If remaining data is exactly 1016 bytes it is capped to 1015 to force the latch.
    /// </summary>
    public bool SendFrame(System.ReadOnlySpan<byte> jpeg)
    {
        lock (_lock)
        {
            if (_device is null) return false;

            var lcdSlice = _writeBuffer.AsSpan(0, LcdBufferSize);
            var offset = 0;
            var total = jpeg.Length;
            var chunkIndex = 0;

            while (offset < total)
            {
                var remaining = total - offset;
                var isLast = remaining <= MaxPayloadPerChunk;
                var chunkSize = ComputeChunkSize(offset, total, isLast);

                lcdSlice.Clear();
                FillChunkHeader(lcdSlice, chunkIndex, chunkSize, isLast);
                jpeg.Slice(offset, chunkSize).CopyTo(lcdSlice.Slice(LcdHeaderSize));

                if (!_device.Write(_writeBuffer))
                {
                    return false;
                }

                offset += chunkSize;
                chunkIndex++;
            }
            return true;
        }
    }

    public bool SetBrightness(byte value)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            return _device.SetFeature(BuildBrightnessReport(value));
        }
    }

    public bool SetRotation(byte value)
    {
        lock (_lock)
        {
            if (_device is null) return false;
            return _device.SetFeature(BuildRotationReport(value));
        }
    }

    // Three-step shutdown sequence per lsh.go:659-677.
    private void SendShutdown()
    {
        _device!.SetFeature(new byte[] { 0x03, 0x1e, 0x01, 0x01 });
        _device!.SetFeature(new byte[] { 0x03, 0x1d, 0x00, 0x01 });
        _device!.SetFeature(new byte[] { 0x03, 0x0b, 0x64, 0x01 });
    }

    /// <summary>
    /// Returns chunk payload size. For the last chunk, caps exactly-1016 to 1015 so
    /// the firmware always receives a sub-1016 final packet and renders the frame.
    /// </summary>
    internal static int ComputeChunkSize(int offset, int totalLength, bool isLast)
    {
        if (!isLast) return MaxPayloadPerChunk;
        var remaining = totalLength - offset;
        return remaining == MaxPayloadPerChunk ? MaxPayloadPerChunk - 1 : remaining;
    }

    /// <summary>
    /// Fills the 8-byte LCD header at <paramref name="buffer"/>[0..7]; buffer[0]=0x02 is the
    /// HID report id. Layout: [0x02, 0x05, 0x01, isLast(0/1), chunkIdx&amp;0xFF, 0x00, lenLo, lenHi].
    /// </summary>
    internal static void FillChunkHeader(System.Span<byte> buffer, int chunkIndex, int dataLen, bool isLast)
    {
        buffer[0] = 0x02;
        buffer[1] = 0x05;
        buffer[2] = 0x01;
        buffer[3] = isLast ? (byte)0x01 : (byte)0x00;
        buffer[4] = (byte)(chunkIndex & 0xFF);
        buffer[5] = 0x00;
        buffer[6] = (byte)(dataLen & 0xFF);
        buffer[7] = (byte)((dataLen >> 8) & 0xFF);
    }

    internal static byte[] BuildBrightnessReport(byte value) =>
        new byte[] { 0x03, 0x0b, value, 0x01 };

    internal static byte[] BuildRotationReport(byte value) =>
        new byte[] { 0x03, 0x0c, value, 0x01 };
}
