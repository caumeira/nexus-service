using System;
using System.IO;
using System.Threading;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA frame stream into JPEG pushes on the iCUE LINK
/// cooler LCD. Same shape as <see cref="JpegPanelStreamTransport"/> - that family's panels
/// are claimed by their own hub, while this one shares <see cref="CorsairLinkLcd"/> with the
/// still/GIF media worker, so opening here takes ownership and disposing hands it back.
///
/// The interface is a byte stream, not a frame queue: the paced writer may split or
/// concatenate writes, so this buffers to exactly one frame before encoding. Frames are
/// fixed-size, which makes the boundary unambiguous.
/// </summary>
public sealed class CorsairLinkLcdStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    private readonly CorsairLinkLcd _lcd;
    private readonly BgraJpegEncoder _encoder;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;
    private bool _owned;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

    // This firmware clears the panel when frames stop arriving - OpenLinkHub re-sends a
    // still every 10 ms for exactly that reason (lsh.go:5071) - and the render side only
    // produces 30. So the last frame is kept and re-pushed between real ones. It expires
    // shortly after the renderer goes quiet, so a stalled overlay hands the glass back to
    // the media worker instead of being propped up forever.
    private const int KeepaliveMs = 10;
    private const long KeepaliveGraceMs = 1000;
    private readonly object _lastFrameLock = new();
    private byte[] _lastFrame = Array.Empty<byte>();
    private int _lastFrameLength;
    private long _lastRealFrameMs;
    private Timer? _keepalive;

    public CorsairLinkLcdStreamTransport(CorsairLinkLcd lcd, string serial)
    {
        _lcd = lcd;
        Serial = serial;
        _encoder = new BgraJpegEncoder(CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight);
        _frame = new byte[_encoder.FrameBytes];
    }

    public bool IsOpen => !_disposed && _lcd.HasDevice;

    public string Serial { get; }

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    public void Open()
    {
        if (!_lcd.HasDevice)
        {
            throw new IOException("corsair iCUE LINK LCD not attached");
        }
        _filled = 0;
        _owned = true;
        _lcd.ClaimStream(this);
        _keepalive = new Timer(_ => Keepalive(), null, KeepaliveMs, KeepaliveMs);
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
            jpeg = _encoder.Encode(
                _orientation.Apply(_frame, CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight));
        }
        catch (Exception ex)
        {
            // An encode failure is a bug in this pipeline, not a device fault; log it once
            // and keep the stream alive so the next frame still gets a chance.
            if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[corsair-lcd] frame encode failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        lock (_lastFrameLock)
        {
            if (_lastFrame.Length < jpeg.Length)
            {
                _lastFrame = new byte[jpeg.Length];
            }
            jpeg.CopyTo(_lastFrame);
            _lastFrameLength = jpeg.Length;
            _lastRealFrameMs = Environment.TickCount64;
        }
        _lcd.NoteStreamFrame(this);

        if (_lcd.SendFrame(jpeg))
        {
            _dropLogged = false;
        }
        else if (!_dropLogged)
        {
            _dropLogged = true;
            ServiceLog.Warn("[corsair-lcd] frame rejected; retrying on the next frame");
        }
    }

    /// <summary>
    /// Re-pushes the last rendered frame so the panel keeps latching between the render
    /// side's own frames. Silent once the renderer has been quiet past the grace window.
    /// </summary>
    private void Keepalive()
    {
        if (_disposed)
        {
            return;
        }
        lock (_lastFrameLock)
        {
            if (_lastFrameLength == 0
                || Environment.TickCount64 - _lastRealFrameMs > KeepaliveGraceMs)
            {
                return;
            }
            _lcd.SendFrame(_lastFrame.AsSpan(0, _lastFrameLength));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _keepalive?.Dispose();
        _keepalive = null;
        if (_owned)
        {
            // Hand the glass back to the media worker, which repaints the selected still
            // or GIF on its next pass. By identity: a transport that faulted after its
            // replacement opened must not release the live one.
            _lcd.ReleaseStream(this);
        }
        _encoder.Dispose();
    }
}
