using System;
using System.IO;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink that turns the overlay's raw BGRA frame stream into Kraken LCD pushes.
///
/// The interface is a byte stream, not a frame queue: the paced writer may split or
/// concatenate writes, so this buffers to exactly one frame before pushing. Frames are
/// fixed-size, which makes the boundary unambiguous.
///
/// The overlay produces BGRA (what the compositor hands back); the cooler wants RGBA with
/// a zero alpha byte, so the swap happens here rather than costing a pass on the GPU side.
/// </summary>
public sealed class KrakenStreamTransport : IStreamedPanelTransport
{
    private readonly KrakenHub _hub;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;

    public KrakenStreamTransport(KrakenHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        _frame = new byte[KrakenProtocol.LcdFrameBytes];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected && _hub.HasLcd;

    public string Serial { get; }

    public void Open()
    {
        if (!_hub.IsConnected)
        {
            throw new IOException("kraken not connected");
        }
        if (!_hub.HasLcd)
        {
            throw new IOException("kraken LCD bulk pipe unavailable");
        }
        _filled = 0;
    }

    /// <summary>The panel has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(KrakenStreamTransport));
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
            SwapToRgbaInPlace(_frame);
            if (_hub.PushStreamFrame(_frame))
            {
                _dropLogged = false;
            }
            else if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn("[nzxt-kraken] stream frame rejected; retrying on the next frame");
            }
        }
    }

    /// <summary>BGRA to RGBA, forcing alpha to 0 (any other value mangles the colours).</summary>
    private static void SwapToRgbaInPlace(byte[] frame)
    {
        for (int i = 0; i + 3 < frame.Length; i += 4)
        {
            (frame[i], frame[i + 2]) = (frame[i + 2], frame[i]);
            frame[i + 3] = 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
