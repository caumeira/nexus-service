using System;
using System.IO;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA frame stream into JPEG uploads on a cooler LCD.
///
/// The interface is a byte stream, not a frame queue: the paced writer may split or
/// concatenate writes, so this buffers to exactly one frame before encoding. Frames are
/// fixed-size, which makes the boundary unambiguous.
/// </summary>
public sealed class JpegPanelStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    private readonly JpegPanelHub _hub;
    private readonly BgraJpegEncoder _encoder;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

    // Only allocated for a model whose glass is turned; the rest encode straight from the
    // oriented frame.
    private readonly byte[] _turned;

    public JpegPanelStreamTransport(JpegPanelHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        var model = hub.Model;
        _encoder = new BgraJpegEncoder(model.Width, model.Height);
        _frame = new byte[_encoder.FrameBytes];
        _turned = model.QuarterTurnsCcw == 0 ? Array.Empty<byte>() : new byte[_frame.Length];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

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
        ReadOnlySpan<byte> jpeg;
        try
        {
            var model = _hub.Model;
            // The record's flip/mirror acts on canonical upright content; the model's own
            // quarter turn is the panel's quirk and so goes last, closest to the glass.
            var oriented = _orientation.Apply(_frame, model.Width, model.Height);
            if (model.QuarterTurnsCcw != 0)
            {
                BgraQuarterTurn.RotateCcw(oriented, model.Width, model.Height, _turned);
                oriented = _turned;
            }
            jpeg = _encoder.Encode(oriented);
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
        _encoder.Dispose();
    }
}
