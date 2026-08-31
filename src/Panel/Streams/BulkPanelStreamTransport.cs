using System;
using System.IO;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA stream into frames on a bulk-pipe cooler LCD.
/// Buffers to exactly one frame before handing it to the driver, which owns whatever
/// encoding its panel wants.
/// </summary>
public sealed class BulkPanelStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    private readonly BulkPanelHub _hub;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

    public BulkPanelStreamTransport(BulkPanelHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        // Fixed at construction from the geometry the driver negotiated; discovery only
        // reports a panel once that is known.
        _frame = new byte[Math.Max(0, hub.FrameBytes)];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    public string Serial { get; }

    public void Open()
    {
        if (!_hub.IsConnected)
        {
            throw new IOException($"{_hub.Driver.Name} not connected");
        }
        if (_frame.Length <= 0)
        {
            // A zero-length frame would make Write spin forever consuming nothing.
            throw new IOException($"{_hub.Driver.Name} panel size unknown");
        }
        _filled = 0;
    }

    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frame.Length <= 0)
        {
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
            if (_hub.SendFrame(_orientation.Apply(_frame, _hub.Width, _hub.Height)))
            {
                _dropLogged = false;
            }
            else if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[{_hub.Driver.HandlerId}] frame rejected; retrying on the next frame");
            }
        }
    }

    public void Dispose() => _disposed = true;
}
