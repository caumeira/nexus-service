using System;
using System.IO;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink for cooler LCDs. JPEG panels receive raw BGRA frames and are encoded here;
/// Galahad II Vision receives the overlay's H.264 access units directly.
///
/// The raw-BGRA path is a byte stream rather than a frame queue, so it buffers to exactly
/// one fixed-size frame before encoding. The H.264 profile keeps writes at one access unit.
/// </summary>
public sealed class JpegPanelStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport, IBrightnessPanelTransport
{
    private readonly JpegPanelHub _hub;
    private readonly BgraJpegEncoder? _encoder;
    private readonly byte[] _frame;
    private readonly bool _usesH264;
    private int _filled;
    private bool _disposed;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

    /// <summary>How stale the polled backlight may get; reading it clones the panel record.</summary>
    private const long BrightnessTtlMs = 500;

    private Func<int?>? _brightness;
    // Seeded from what the panel was last told, so a reconnect re-sends nothing it has.
    private int _brightnessApplied = -1;
    private long _brightnessNextReadMs;
    private bool _brightnessFaultLogged;
    private readonly object _brightnessLock = new();

    private readonly byte[] _turned;

    public JpegPanelStreamTransport(JpegPanelHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        var model = hub.Model;
        _usesH264 = model.FrameEncoding == JpegPanelFrameEncoding.H264;
        _encoder = _usesH264 ? null : new BgraJpegEncoder(model.Width, model.Height);
        _frame = _encoder is null ? Array.Empty<byte>() : new byte[_encoder.FrameBytes];
        _turned = model.QuarterTurnCcw ? new byte[_frame.Length] : Array.Empty<byte>();
    }

    public bool IsOpen => !_disposed && _hub.IsConnected;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    /// <summary>Ignored for a panel with no backlight command, so those never pay the poll.</summary>
    public void BindBrightness(Func<int?> source)
    {
        if (!_hub.Model.SupportsBrightness)
        {
            return;
        }
        _brightness = source;
        _brightnessApplied = _hub.Brightness;
        _brightnessNextReadMs = 0;
    }

    /// <summary>Applies a changed backlight from either the frame or settings path.</summary>
    public bool ApplyBrightness()
    {
        lock (_brightnessLock)
        {
            _brightnessNextReadMs = 0;
            return TryApplyBrightnessLocked();
        }
    }

    private bool TryApplyBrightness()
    {
        lock (_brightnessLock)
        {
            return TryApplyBrightnessLocked();
        }
    }

    private bool TryApplyBrightnessLocked()
    {
        if (_brightness is null)
        {
            return false;
        }
        long nowMs = Environment.TickCount64;
        if (nowMs < _brightnessNextReadMs)
        {
            return false;
        }
        _brightnessNextReadMs = nowMs + BrightnessTtlMs;
        int? wanted;
        try { wanted = _brightness(); }
        catch (Exception ex)
        {
            if (!_brightnessFaultLogged)
            {
                _brightnessFaultLogged = true;
                ServiceLog.Warn($"[{_hub.Model.HandlerId}] backlight source threw: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
        // No record value means the panel keeps what it powered up with.
        if (wanted is not int percent)
        {
            return true;
        }
        if (percent == _brightnessApplied)
        {
            return true;
        }
        if (_hub.SetBrightness(percent))
        {
            _brightnessApplied = percent;
            ServiceLog.Info($"[{_hub.Model.HandlerId}] backlight {percent}%");
            return true;
        }
        return false;
    }

    public string Serial { get; }

    public void Open()
    {
        if (!_hub.IsConnected)
        {
            throw new IOException($"{_hub.Model.Name} not connected");
        }
        _filled = 0;
    }

    /// <summary>The panel has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_usesH264)
        {
            TryApplyBrightness();
            if (_hub.SendFrame(payload))
            {
                _dropLogged = false;
            }
            else if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[{_hub.Model.HandlerId}] frame rejected; retrying on the next frame");
            }
            return;
        }

        while (!payload.IsEmpty)
        {
            int take = Math.Min(_frame.Length - _filled, payload.Length);
            payload[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            payload = payload[take..];

            if (_filled < _frame.Length)
            {
                continue;
            }
            _filled = 0;
            PushFrame();
        }
    }

    private void PushFrame()
    {
        TryApplyBrightness();
        ReadOnlySpan<byte> jpeg;
        try
        {
            var encoder = _encoder;
            if (encoder is null)
            {
                return;
            }
            var model = _hub.Model;
            // The record's flip/mirror acts on canonical upright content; the model's own
            // quarter turn is the panel's quirk and so goes last, closest to the glass.
            var oriented = _orientation.Apply(_frame, model.Width, model.Height);
            if (model.QuarterTurnCcw)
            {
                BgraQuarterTurn.RotateCcw(oriented, model.Width, model.Height, _turned);
                oriented = _turned;
            }
            jpeg = encoder.Encode(oriented);
        }
        catch (Exception ex)
        {
            // An encode failure is a bug in this pipeline, not a device fault; log it once
            // and keep the stream alive so the next frame still gets a chance.
            if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[{_hub.Model.HandlerId}] frame encode failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        if (_hub.SendFrame(jpeg))
        {
            _dropLogged = false;
        }
        else if (!_dropLogged)
        {
            _dropLogged = true;
            ServiceLog.Warn($"[{_hub.Model.HandlerId}] frame rejected; retrying on the next frame");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _encoder?.Dispose();
    }
}
