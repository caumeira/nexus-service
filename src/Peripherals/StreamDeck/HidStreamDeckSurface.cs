using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Real Stream Deck backed by the existing hand-rolled HID stack
/// (<see cref="IHidEnumerator"/> / <see cref="IHidDevice"/>). Mirrors the
/// KeebHub pattern (src/Peripherals/Hyte/Keeb/KeebHub.cs): all IO serialised
/// on <c>_io</c>, a reused input-report buffer, write-failure backoff that
/// drops the handle after a run of consecutive failures so the connection
/// worker re-opens on the next tick.
///
/// The Mini exposes a single HID top-level collection (UsagePage 0x0C, Usage
/// 0x01, bench-confirmed 2026-07-10), so one handle carries feature writes,
/// image output reports, and interrupt-IN input reads.
/// </summary>
public sealed class HidStreamDeckSurface : IStreamDeckSurface
{
    private const int ConsecutiveWriteFailureThreshold = 5;

    private readonly IHidEnumerator _hid;
    private readonly object _io = new();
    private IHidDevice? _device;
    private readonly byte[] _inputBuf;
    private int _consecutiveWriteFailures;

    public StreamDeckModel Model { get; }
    public string Serial { get; private set; } = "";
    public string FirmwareVersion { get; private set; } = "";
    public bool IsConnected => _device is not null;

    public HidStreamDeckSurface(IHidEnumerator hid, StreamDeckModel model)
    {
        _hid = hid;
        Model = model;
        _inputBuf = new byte[1 + model.KeyCount];
    }

    /// <summary>Opens the device at the given HID interface. False if already open or the open failed.</summary>
    public bool Connect(HidDeviceInfo info)
    {
        lock (_io)
        {
            if (_device is not null)
            {
                return true;
            }
            var dev = _hid.Open(info.Path, forInput: true);
            if (dev is null)
            {
                ServiceLog.Error($"[streamdeck] open failed for {info.Path} ({Model.Name})");
                return false;
            }
            _device = dev;
            Serial = !string.IsNullOrWhiteSpace(info.Serial) ? info.Serial! : StableIdFromPath(info.Path);
            _consecutiveWriteFailures = 0;
            ReadFirmwareVersionLocked();
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_io)
        {
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
    }

    public bool SetBrightness(int percent)
    {
        lock (_io)
        {
            if (_device is null)
            {
                return false;
            }
            if (_device.SetFeature(StreamDeckProtocol.BuildBrightnessFeature(percent)))
            {
                _consecutiveWriteFailures = 0;
                return true;
            }
            return RecordWriteFailureLocked("brightness");
        }
    }

    public bool Reset()
    {
        lock (_io)
        {
            if (_device is null)
            {
                return false;
            }
            if (_device.SetFeature(StreamDeckProtocol.BuildResetFeature()))
            {
                _consecutiveWriteFailures = 0;
                return true;
            }
            return RecordWriteFailureLocked("reset");
        }
    }

    public bool SetKeyImage(int keyIndex, ReadOnlyMemory<byte> wireBytes)
    {
        lock (_io)
        {
            if (_device is null || keyIndex < 0 || keyIndex >= Model.KeyCount)
            {
                return false;
            }
            var rawIndex = Model.RemapKeyIndex(keyIndex);
            var pages = StreamDeckProtocol.BuildImagePages(wireBytes.Span, rawIndex, Model);
            foreach (var page in pages)
            {
                if (!_device.Write(page))
                {
                    return RecordWriteFailureLocked("image-page");
                }
            }
            _consecutiveWriteFailures = 0;
            return true;
        }
    }

    public bool ClearKey(int keyIndex)
    {
        // The blank-image constant only covers the 80x80 BMP family (Mini and
        // its siblings); other models get no blank until a matching image is
        // available (or Phase 2 wires the web-rendered blank through the same path).
        if (Model.ImageFormat != StreamDeckImageFormat.Bmp || Model.KeyPixelSize != 80)
        {
            return false;
        }
        return SetKeyImage(keyIndex, StreamDeckProtocol.BuildBlankBmp(Model.KeyPixelSize));
    }

    public bool[]? ReadInput(int timeoutMs)
    {
        lock (_io)
        {
            if (_device is null)
            {
                return null;
            }
            var n = _device.Read(_inputBuf, timeoutMs);
            if (n < 0)
            {
                ServiceLog.Error($"[streamdeck] {Model.Name} (serial={Serial}) read failed, dropping interface");
                try { _device.Dispose(); } catch { /* best effort */ }
                _device = null;
                return null;
            }
            if (n == 0)
            {
                return null;
            }
            return StreamDeckProtocol.DecodeGen1Input(_inputBuf.AsSpan(0, n), Model);
        }
    }

    // Caller holds _io.
    private void ReadFirmwareVersionLocked()
    {
        var dev = _device!;
        try
        {
            var request = StreamDeckProtocol.BuildFirmwareFeatureRequest();
            if (!dev.GetFeature(request))
            {
                return;
            }
            FirmwareVersion = StreamDeckProtocol.ExtractAsciiString(request);
        }
        catch { /* best effort - firmware version is cosmetic */ }
    }

    // A single dropped report is transient (USB jitter); only tear down the
    // interface after a sustained run so the next connection-worker tick
    // re-enumerates. Caller must hold _io.
    private bool RecordWriteFailureLocked(string where)
    {
        var n = ++_consecutiveWriteFailures;
        if (n >= ConsecutiveWriteFailureThreshold)
        {
            ServiceLog.Error($"[streamdeck] {Model.Name} (serial={Serial}): {n} consecutive write failures ({where}) - dropping interface");
            _consecutiveWriteFailures = 0;
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }
        return false;
    }

    private static string StableIdFromPath(string path)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in path) { h ^= c; h *= 16777619; }
            return $"sd-{h:x8}";
        }
    }

    public void Dispose() => Disconnect();
}
