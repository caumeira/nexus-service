using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Owns the Kraken's HID control channel and its WinUSB bulk pipe, and serializes every
/// exchange. HID I/O happens under <c>_lock</c>; the LCD's bulk pixel writes run outside it
/// under <c>_lcdTransferLock</c>, since they use a different endpoint. Callers outside the
/// worker thread read <see cref="Snapshot"/> without blocking.
/// </summary>
public sealed class KrakenHub : IDisposable
{
    private const int ConnectReadTimeoutMs = 1000;
    private const int CommandReadTimeoutMs = 700;
    private const int PollReadTimeoutMs = 250;

    // Bulk pixel data goes out in chunks; 64 KiB measured ~13 MB/s on the bench unit.
    private const int BulkChunkBytes = 64 * 1024;

    private readonly object _lock = new();
    // Serialises whole LCD transfers with each other. The stream path holds this across a
    // frame while taking _lock only for the short HID steps, so the ~414 ms of pixels does
    // not block the lighting writer: pixels ride the bulk endpoint, colours ride HID.
    private readonly object _lcdTransferLock = new();
    private readonly IKrakenLcdTransportFactory? _lcdFactory;
    private IHidDevice? _device;
    private IKrakenLcdTransport? _lcd;
    private bool _disposed;

    private volatile bool _isConnected;
    private volatile KrakenSnapshot _snapshot = KrakenSnapshot.Empty;
    // Last image pushed, stored unrotated so a rotation change can re-render it.
    private byte[]? _lastLcdFrame;

    // Streaming state. Two buckets are allocated once, then alternated: the panel
    // rejects a transfer into the bucket it is currently displaying (code 9), so a
    // stream has to write the idle one and switch to it.
    private bool _streamReady;
    private int _streamActiveBucket = -1;

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

    /// <summary>USB serial of the attached cooler, or null when nothing is attached.</summary>
    public string? Serial
    {
        get
        {
            lock (_lock)
            {
                return _device?.Serial;
            }
        }
    }

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
            // A channel is a chain: each slot is one accessory daisied off the last. A
            // multi-fan radiator reports as ONE accessory covering the whole radiator
            // (an F240 is a single 0x1B, not two entries), so the slots past the first
            // are only populated when the user has chained more hardware onto the port.
            byte first = 0;
            int leds = 0;
            int rings = 0;
            int accessories = 0;
            for (int slot = 0; slot < KrakenProtocol.AccessorySlotsPerChannel; slot++)
            {
                byte accessory = KrakenProtocol.DecodeAccessory(reply, channel, slot);
                if (accessory == 0)
                {
                    continue;
                }
                if (accessories == 0)
                {
                    first = accessory;
                }
                accessories++;
                leds += KrakenProtocol.LedCountForAccessory(accessory);
                var (accRings, _) = KrakenProtocol.AccessoryRings(accessory);
                rings += accRings;
            }
            if (accessories == 0)
            {
                continue;
            }
            var name = KrakenProtocol.AccessoryName(first);
            if (accessories > 1)
            {
                name = $"{name} x{accessories}";
            }
            channels.Add(new KrakenLightingChannel((byte)(1 << channel), first, name, leds, rings));
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
        // A rotation re-uploads the stored frame, which is a full bulk transfer, so this
        // takes the transfer lock first - same order as PushStreamFrame, never the reverse.
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || !_device.Write(report))
            {
                return false;
            }
            int turns = orientationQuarterTurns & 0x03;
            bool rotated = turns != _snapshot.LcdOrientationQuarterTurns;
            _snapshot = _snapshot.WithLcd(Math.Clamp(brightnessPercent, 0, 100), turns);
            // The panel will not re-orient a stored image on its own.
            if (rotated && _lastLcdFrame != null && _snapshot.DisplayMode == KrakenDisplayMode.Bucket)
            {
                UploadLcdFrameLocked(_lastLcdFrame, turns);
            }
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

    /// <summary>
    /// Sets every LED on a channel with one report. The firmware applies it on arrival, so
    /// there is no latch, no apply command, and nothing to wait for; the NAK this answers
    /// with carries no information (it answers writes that visibly land).
    /// </summary>
    public bool SetDirectColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        var report = KrakenProtocol.EncodeChannelColors(channelId, rgbColors);
        lock (_lock)
        {
            return _device != null && _device.Write(report);
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

        var source = rgba.ToArray();
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || _lcd == null)
            {
                return false;
            }
            _lastLcdFrame = source;
            _streamReady = false;
            return UploadLcdFrameLocked(source, _snapshot.LcdOrientationQuarterTurns);
        }
    }

    private bool UploadLcdFrameLocked(byte[] source, int quarterTurns)
    {
        var lcd = _lcd;
        if (_device == null || lcd == null)
        {
            return false;
        }
        var payload = KrakenProtocol.RotateRgba(source, KrakenProtocol.LcdWidth, KrakenProtocol.LcdHeight, quarterTurns);
        var header = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgba8888, payload.Length);
        int pages = KrakenProtocol.PagesFor(payload.Length);
        {
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
            if (!lcd.Write(header))
            {
                return false;
            }
            for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
            {
                int len = Math.Min(BulkChunkBytes, payload.Length - offset);
                if (!lcd.Write(payload.AsSpan(offset, len)))
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
    /// Pushes one frame for a live stream, double-buffered. Allocates its two buckets on
    /// the first call and alternates thereafter; unlike <see cref="UploadLcdImage"/> this
    /// does not wipe the bucket table per frame, which is what makes a stream viable.
    ///
    /// Measured ceiling on the Elite V2 is ~2.3 fps: a full 640x640 RGBA frame is 1.6 MB
    /// and the bulk pipe accepts about 4 MB/s, which dominates the ~24 ms of handshake.
    /// </summary>
    public bool PushStreamFrame(ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != KrakenProtocol.LcdFrameBytes)
        {
            return false;
        }
        var source = rgba.ToArray();
        lock (_lcdTransferLock)
        {
            int target;
            byte[] payload;
            IKrakenLcdTransport lcd;
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                if (!_streamReady && !PrepareStreamBucketsLocked())
                {
                    return false;
                }
                lcd = _lcd;
                target = _streamActiveBucket == 0 ? 1 : 0;
                payload = KrakenProtocol.RotateRgba(
                    source, KrakenProtocol.LcdWidth, KrakenProtocol.LcdHeight, _snapshot.LcdOrientationQuarterTurns);
                var start = ExchangeLocked(
                    KrakenProtocol.EncodeStartTransfer(target), 0x37, 0x01, CommandReadTimeoutMs);
                if (start == null || !KrakenProtocol.IsAck(start))
                {
                    _streamReady = false;
                    return false;
                }
            }

            // Bulk endpoint only, outside _lock: HID commands for lighting and telemetry
            // keep flowing while the pixels stream, which is what lets an animation run at
            // its own rate instead of waiting a whole frame for the pipe.
            if (!WriteBulkPayload(lcd, payload))
            {
                lock (_lock) { _streamReady = false; }
                return false;
            }

            lock (_lock)
            {
                if (_device == null)
                {
                    return false;
                }
                ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);
                var activate = ExchangeLocked(
                    KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, target), 0x39, 0x01, CommandReadTimeoutMs);
                if (activate == null || !KrakenProtocol.IsAck(activate))
                {
                    // Re-allocate next call: a rejected transfer usually means the bucket
                    // table no longer matches what this hub believes.
                    _streamReady = false;
                    return false;
                }
                _streamActiveBucket = target;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                return true;
            }
        }
    }

    /// <summary>Header then pixels on the bulk endpoint; takes no lock of its own.</summary>
    private static bool WriteBulkPayload(IKrakenLcdTransport lcd, byte[] payload)
    {
        var header = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgba8888, payload.Length);
        // The header must be its own bulk transfer; concatenating corrupts the upload.
        if (!lcd.Write(header))
        {
            return false;
        }
        for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
        {
            int len = Math.Min(BulkChunkBytes, payload.Length - offset);
            if (!lcd.Write(payload.AsSpan(offset, len)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Clears the bucket table and reserves two non-overlapping frame slots.</summary>
    private bool PrepareStreamBucketsLocked()
    {
        int pages = KrakenProtocol.PagesFor(KrakenProtocol.LcdFrameBytes);
        ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
        for (int i = 0; i < KrakenProtocol.BucketCount; i++)
        {
            ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
        }
        for (int bucket = 0; bucket < 2; bucket++)
        {
            var setup = ExchangeLocked(
                KrakenProtocol.EncodeSetupBucket(bucket, bucket * pages, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (setup == null || !KrakenProtocol.IsAck(setup))
            {
                ServiceLog.Warn($"[nzxt-kraken] stream bucket {bucket} setup rejected");
                return false;
            }
        }
        _streamActiveBucket = -1;
        _streamReady = true;
        return true;
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
