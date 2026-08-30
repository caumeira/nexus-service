using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Thermalright's LCD coolers. The panel identifies itself at connect, so this is the one
/// driver whose geometry is negotiated rather than declared - a model we have never heard
/// of still reports a size we can render at, provided it is in the table.
/// </summary>
public sealed class ThermalrightPanelDriver : IBulkPanelDriver
{
    private const int InitAttempts = 10;
    private const int ReplyTimeoutMs = 500;

    private ThermalrightPanel _panel;
    private BgraJpegEncoder? _jpeg;
    private byte[] _rgb565 = Array.Empty<byte>();

    public string HandlerId => "thermalright-lcd";
    public string Name => _panel.Name is { Length: > 0 } n ? $"Thermalright {n}" : "Thermalright LCD";
    public int VendorId => 0x87AD;
    public IReadOnlyList<int> ProductIds { get; } = new[] { 0x70DB };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;
    public int Fps => 30;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x81;
    public bool NeedsHidChannel => false;

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        var request = ThermalrightProtocol.EncodeInitRequest();
        var reply = new byte[64];

        for (int attempt = 0; attempt < InitAttempts; attempt++)
        {
            if (!pipe.Write(request))
            {
                return null;
            }
            int read = pipe.Read(reply, ReplyTimeoutMs);
            if (read < 0)
            {
                return null;
            }
            if (read == 0)
            {
                continue;
            }
            var span = reply.AsSpan(0, read);
            if (ThermalrightProtocol.IsBooting(span))
            {
                // The panel answers before it is ready; it needs seconds to settle.
                continue;
            }
            var modelId = ThermalrightProtocol.DecodeModelId(span);
            if (modelId is null)
            {
                continue;
            }
            var panel = ThermalrightProtocol.PanelFor(modelId.Value);
            if (panel is null)
            {
                ServiceLog.Warn(
                    $"[{HandlerId}] panel reports model 0x{modelId.Value:X2}, which is not in the table");
                return null;
            }
            return Adopt(panel.Value);
        }

        // The Frozen Warframe Pro answers no init at all, so silence is a positive result
        // for exactly one model rather than a failure.
        ServiceLog.Info($"[{HandlerId}] no init reply; assuming the model that never answers one");
        var fallback = ThermalrightProtocol.PanelFor(ThermalrightProtocol.FallbackModelId);
        return fallback is null ? null : Adopt(fallback.Value);
    }

    private (int Width, int Height) Adopt(ThermalrightPanel panel)
    {
        _panel = panel;
        _jpeg?.Dispose();
        _jpeg = panel.Rgb565 ? null : new BgraJpegEncoder(panel.Width, panel.Height);
        _rgb565 = panel.Rgb565 ? new byte[panel.Width * panel.Height * 2] : Array.Empty<byte>();
        ServiceLog.Info($"[{HandlerId}] {panel.Name}, {panel.Width}x{panel.Height}, {(panel.Rgb565 ? "RGB565" : "JPEG")}");
        return (panel.Width, panel.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (_panel.Width <= 0)
        {
            return false;
        }
        ReadOnlySpan<byte> payload;
        if (_panel.Rgb565)
        {
            int written = Rgb565Encoder.Encode(
                bgra, _panel.Width, _panel.Height, quarterTurns: 0, _rgb565, sourceIsBgra: true);
            payload = _rgb565.AsSpan(0, written);
        }
        else
        {
            if (_jpeg is null)
            {
                return false;
            }
            payload = _jpeg.Encode(bgra);
        }

        var header = ThermalrightProtocol.EncodeFrameHeader(
            _panel.Width, _panel.Height, payload.Length, _panel.Rgb565);

        // Header and payload go out as ONE transfer here. That is the opposite of the
        // Kraken, where concatenating them corrupts the upload; this controller wants it.
        var packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet.AsSpan(header.Length));
        return pipe.Write(packet);
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _jpeg?.Dispose();
        _jpeg = null;
        _panel = default;
    }
}
