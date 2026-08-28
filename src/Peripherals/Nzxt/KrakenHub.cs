using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Owns the Kraken's HID control channel and its WinUSB bulk pipe, and serializes every
/// exchange. All I/O happens under <c>_lock</c>; callers outside the worker thread read
/// <see cref="Snapshot"/> without blocking.
/// </summary>
public sealed class KrakenHub : IDisposable
{
    private const int ConnectReadTimeoutMs = 1000;
    private const int CommandReadTimeoutMs = 700;
    private const int PollReadTimeoutMs = 250;

    // Bulk pixel data goes out in chunks; 64 KiB measured ~13 MB/s on the bench unit.
    private const int BulkChunkBytes = 64 * 1024;

    private readonly object _lock = new();
    private readonly IKrakenLcdTransportFactory? _lcdFactory;
    private IHidDevice? _device;
    private IKrakenLcdTransport? _lcd;
    private bool _disposed;

    private volatile bool _isConnected;
    private volatile KrakenSnapshot _snapshot = KrakenSnapshot.Empty;

    public KrakenHub(IKrakenLcdTransportFactory? lcdFactory = null)
    {
        _lcdFactory = lcdFactory;
    }

    public const string DeviceId = "nzxt-kraken";
    public const string ProductName = "NZXT Kraken";

    /// <summary>Lighting zone ids. Kept distinct from the cooling channel ids on the same device.</summary>
    public const string RingZoneId = "nzxt-kraken:led-ring";
    public const string FansZoneId = "nzxt-kraken:led-fans";

    public bool IsConnected => _isConnected;

    /// <summary>Read this reference once, then use it for all field accesses.</summary>
    public KrakenSnapshot Snapshot => _snapshot;

    /// <summary>Zone id for a channel index, matching the order channels are reported in.</summary>
    public static string ZoneIdForChannelIndex(int channelIndex) =>
        channelIndex == 0 ? RingZoneId : FansZoneId;

    /// <summary>True when the bulk pipe opened, i.e. LCD image upload is available.</summary>
    public bool HasLcd
    {
        get
        {
            lock (_lock)
            {
                return _lcd != null;
            }
        }
    }

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
        }
    }

    /// <summary>
    /// Reads the static device facts and marks the hub connected. Called from the worker
    /// thread only.
    /// </summary>
    public bool Connect()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }

            var fwReply = ExchangeLocked(KrakenProtocol.EncodeFirmwareRequest(), 0x11, 0x01, ConnectReadTimeoutMs);
            if (fwReply == null)
            {
                return false;
            }
            var fw = KrakenProtocol.DecodeFirmware(fwReply);

            // Starts the cooler's telemetry stream. Without it the accessory table is not
            // populated yet and the lighting query answers with zero channels.
            _device.Write(KrakenProtocol.EncodeSetUpdateInterval());
            _device.Write(KrakenProtocol.EncodeStartReporting());

            var channels = ReadLightingChannelsLocked();

            int brightness = 0;
            int orientation = 0;
            var lcdReply = ExchangeLocked(KrakenProtocol.EncodeLcdInfoRequest(), 0x31, 0x01, CommandReadTimeoutMs);
            var lcdInfo = lcdReply == null ? null : KrakenProtocol.DecodeLcdInfo(lcdReply);
            if (lcdInfo.HasValue)
            {
                brightness = lcdInfo.Value.BrightnessPercent;
                orientation = lcdInfo.Value.OrientationQuarterTurns;
            }

            var modeReply = ExchangeLocked(KrakenProtocol.EncodeReadDisplayModeRequest(), 0x31, 0x03, CommandReadTimeoutMs);
            var mode = (modeReply == null ? null : KrakenProtocol.DecodeDisplayMode(modeReply))
                ?? KrakenDisplayMode.Liquid;

            _snapshot = new KrakenSnapshot(
                0, 0, 0, 0, 0,
                fw?.ToString() ?? "",
                brightness, orientation, mode, channels);

            _lcd = _lcdFactory?.Open(_device.Serial);
            if (_lcd == null)
            {
                ServiceLog.Info("[nzxt-kraken] connected without an LCD bulk pipe; image upload unavailable");
            }

            _isConnected = true;
            return true;
        }
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsLocked()
    {
        // The accessory table can answer empty right after the stream starts, and an empty
        // result would leave every RGB zone undrivable for the whole session.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var channels = ReadLightingChannelsOnceLocked();
            if (channels.Count > 0)
            {
                return channels;
            }
        }
        ServiceLog.Warn("[nzxt-kraken] no RGB channels reported; lighting stays unavailable");
        return Array.Empty<KrakenLightingChannel>();
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsOnceLocked()
    {
        var channels = new List<KrakenLightingChannel>();
        var reply = ExchangeLocked(KrakenProtocol.EncodeLightingInfoRequest(), 0x21, 0x03, CommandReadTimeoutMs);
        if (reply == null)
        {
            return channels;
        }
        int count = KrakenProtocol.DecodeChannelCount(reply);
        for (int channel = 0; channel < count; channel++)
        {
            // Only the first accessory slot is populated on this cooler; a fan chain
            // reports as one accessory type covering the whole chain.
            byte accessory = KrakenProtocol.DecodeAccessory(reply, channel, 0);
            if (accessory == 0)
            {
                continue;
            }
            channels.Add(new KrakenLightingChannel(
                (byte)(1 << channel),
                accessory,
                KrakenProtocol.AccessoryName(accessory),
                KrakenProtocol.LedCountForAccessory(accessory)));
        }
        return channels;
    }

    /// <summary>
    /// Requests telemetry and folds it into the snapshot. Returns false only when the
    /// device looks gone, which is the worker's signal to tear the handle down.
    /// </summary>
    public bool Poll()
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            DrainLocked();
            if (!_device.Write(KrakenProtocol.EncodeStatusRequest()))
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[KrakenProtocol.ReportLength];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int n = _device.Read(buf, PollReadTimeoutMs);
                if (n < 0)
                {
                    return false;
                }
                if (n == 0)
                {
                    // A quiet window is not a disconnect.
                    return true;
                }
                var reading = KrakenProtocol.DecodeStatus(buf[..n]);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                    return true;
                }
            }
            return true;
        }
    }

    public bool SetPumpCurve(ReadOnlySpan<byte> duties) => SendCurve(KrakenProtocol.PumpChannel, duties);

    public bool SetFanCurve(ReadOnlySpan<byte> duties) => SendCurve(KrakenProtocol.FanChannel, duties);

    /// <summary>Applies a flat duty by filling every point of the curve.</summary>
    public bool SetPumpDuty(int percent) => SetPumpCurve(FlatCurve(percent, KrakenProtocol.PumpDutyFloor));

    public bool SetFanDuty(int percent) => SetFanCurve(FlatCurve(percent, 0));

    private static byte[] FlatCurve(int percent, int floor)
    {
        byte duty = (byte)Math.Clamp(percent, floor, 100);
        var curve = new byte[KrakenProtocol.CurvePointCount];
        curve.AsSpan().Fill(duty);
        return curve;
    }

    private bool SendCurve(ReadOnlySpan<byte> channel, ReadOnlySpan<byte> duties)
    {
        var report = KrakenProtocol.EncodeSpeedCurve(channel, duties);
        lock (_lock)
        {
            return _device != null && _device.Write(report);
        }
    }

    /// <summary>
    /// Backlight and rotation share one command, so both are always sent together using
    /// the snapshot for whichever value the caller is not changing.
    /// </summary>
    public bool SetLcdBacklight(int brightnessPercent, int orientationQuarterTurns)
    {
        var report = KrakenProtocol.EncodeSetBacklight(brightnessPercent, orientationQuarterTurns);
        lock (_lock)
        {
            if (_device == null || !_device.Write(report))
            {
                return false;
            }
            _snapshot = _snapshot.WithLcd(
                Math.Clamp(brightnessPercent, 0, 100), orientationQuarterTurns & 0x03);
            return true;
        }
    }

    public bool SetDisplayMode(KrakenDisplayMode mode, int bucketIndex = 0)
    {
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            var reply = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(mode, bucketIndex), 0x39, 0x01, CommandReadTimeoutMs);
            if (reply == null || !KrakenProtocol.IsAck(reply))
            {
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(mode);
            return true;
        }
    }

    public bool SetLighting(byte channelId, KrakenColorMode mode, KrakenAnimationSpeed speed, ReadOnlySpan<byte> rgbColors, bool forward = true)
    {
        var report = KrakenProtocol.EncodeColors(channelId, mode, speed, rgbColors, forward);
        lock (_lock)
        {
            return _device != null && _device.Write(report);
        }
    }

    public bool SetFixedColor(byte channelId, byte r, byte g, byte b)
    {
        var report = KrakenProtocol.EncodeFixedColor(channelId, r, g, b);
        lock (_lock)
        {
            return _device != null && _device.Write(report);
        }
    }

    /// <summary>
    /// Drives every LED on a channel individually. The three reports are written back to
    /// back under the lock so no other command can interleave between the colour table and
    /// the command that applies it.
    /// </summary>
    public bool SetDirectColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        var reports = KrakenProtocol.EncodeDirectColors(channelId, rgbColors);
        lock (_lock)
        {
            if (_device == null)
            {
                return false;
            }
            foreach (var report in reports)
            {
                if (!_device.Write(report))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Uploads one 640x640 RGBA frame and makes the panel show it.
    ///
    /// Every bucket is deleted first. That step is not optional: a stale allocation makes
    /// the setup command return success while placing the image somewhere the panel never
    /// renders, which is the failure that makes this sequence look like it works when it
    /// does not.
    ///
    /// The bucket store is flash, and a full cycle measures ~450 ms, so this is for still
    /// images. Do not drive an animation through it.
    /// </summary>
    public bool UploadLcdImage(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != KrakenProtocol.LcdFrameBytes)
        {
            ServiceLog.Warn($"[nzxt-kraken] LCD frame must be {KrakenProtocol.LcdFrameBytes} bytes, got {rgba.Length}");
            return false;
        }

        var header = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgba8888, rgba.Length);
        int pages = KrakenProtocol.PagesFor(rgba.Length);
        var payload = rgba.ToArray();

        lock (_lock)
        {
            if (_device == null || _lcd == null)
            {
                return false;
            }

            // Releases the active bucket so it becomes deletable.
            ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
            for (int i = 0; i < KrakenProtocol.BucketCount; i++)
            {
                ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
            }

            const int bucket = 0;
            var setup = ExchangeLocked(KrakenProtocol.EncodeSetupBucket(bucket, 0, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (setup == null || !KrakenProtocol.IsAck(setup))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket setup rejected");
                return false;
            }

            var start = ExchangeLocked(KrakenProtocol.EncodeStartTransfer(bucket), 0x37, 0x01, CommandReadTimeoutMs);
            if (start == null || !KrakenProtocol.IsAck(start))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD transfer start rejected");
                return false;
            }

            // The header must be its own bulk transfer; concatenating it with the pixels
            // corrupts the upload without any error being reported.
            if (!_lcd.Write(header))
            {
                return false;
            }
            for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
            {
                int len = Math.Min(BulkChunkBytes, payload.Length - offset);
                if (!_lcd.Write(payload.AsSpan(offset, len)))
                {
                    ServiceLog.Warn($"[nzxt-kraken] LCD bulk write failed at offset {offset}");
                    return false;
                }
            }

            ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);

            var activate = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, bucket), 0x39, 0x01, CommandReadTimeoutMs);
            if (activate == null || !KrakenProtocol.IsAck(activate))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket activation rejected");
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
            return true;
        }
    }

    /// <summary>
    /// Writes a command and waits for its reply. Unsolicited status reports arrive on the
    /// same pipe about once a second and are folded into the snapshot rather than discarded.
    /// Returns null when no matching reply arrived.
    /// </summary>
    private byte[]? ExchangeLocked(byte[] request, byte replyReportId, byte replySubCommand, int timeoutMs)
    {
        if (_device == null)
        {
            return null;
        }
        // Fire-and-forget writes (lighting, curves) leave their own replies queued. Reading
        // a stale one as this command's answer is how a healthy exchange reports failure, so
        // clear the queue before asking.
        DrainLocked();
        if (!_device.Write(request))
        {
            return null;
        }
        var buf = new byte[KrakenProtocol.ReportLength];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n = _device.Read(buf, timeoutMs);
            if (n < 0)
            {
                return null;
            }
            if (n == 0)
            {
                continue;
            }
            var span = buf.AsSpan(0, n);
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                }
                continue;
            }
            // A NAK echoes the command it rejected; one raised by a different command must
            // not fail this exchange.
            if (span.Length > 15 && span[0] == KrakenProtocol.ReportNak)
            {
                if (span[14] == request[0] && span[15] == request[1])
                {
                    ServiceLog.Warn($"[nzxt-kraken] device rejected command {span[14]:X2} {span[15]:X2}");
                    return null;
                }
                continue;
            }
            if (span.Length > 1 && span[0] == replyReportId && span[1] == replySubCommand)
            {
                return buf[..n];
            }
        }
        return null;
    }

    /// <summary>
    /// Empties the input queue, folding any telemetry it finds into the snapshot. The bound
    /// is a safety net: the device streams status once a second, so a healthy queue is short.
    /// </summary>
    private void DrainLocked()
    {
        if (_device == null)
        {
            return;
        }
        var buf = new byte[KrakenProtocol.ReportLength];
        for (int i = 0; i < 64; i++)
        {
            int n = _device.Read(buf, 0);
            if (n <= 0)
            {
                return;
            }
            var span = buf.AsSpan(0, n);
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    _snapshot = _snapshot.WithReading(reading.Value);
                }
            }
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _isConnected = false;
            _snapshot = KrakenSnapshot.Empty;
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _isConnected = false;
            _snapshot = KrakenSnapshot.Empty;
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
        }
    }
}
