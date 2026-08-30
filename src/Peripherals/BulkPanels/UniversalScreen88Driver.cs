using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The Lian Li Universal Screen 8.8: a 480x1920 strip whose command headers are
/// DES-encrypted (see <see cref="UniversalScreen88Protocol"/>).
///
/// Every header carries a timestamp the firmware requires to strictly increase, so this
/// driver owns a counter rather than reading the clock twice in the same second.
/// </summary>
public sealed class UniversalScreen88Driver : IBulkPanelDriver
{
    private const int AckTimeoutMs = 100;
    private const int AckAttempts = 50;

    private readonly BgraJpegEncoder _jpeg =
        new(UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height);

    private uint _lastTimestamp;

    public string HandlerId => "lianli-screen88";
    public string Name => "Lian Li Universal Screen 8.8";
    public int VendorId => 0x1CBE;
    public IReadOnlyList<int> ProductIds { get; } = new[] { 0xA088 };

    /// <summary>A 480x1920 strip is nothing like round glass; it rides the Y70's shape.</summary>
    public string Surface => Models.Panel.PanelSurfaces.Y70;

    public int Fps => 30;
    public byte WritePipeId => 0x01;
    public byte ReadPipeId => 0x81;
    public bool NeedsHidChannel => false;

    /// <summary>
    /// Monotonic seconds, clamped to "at least the last value plus one" so two commands
    /// inside one second still differ.
    /// </summary>
    private uint NextTimestamp()
    {
        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _lastTimestamp = now > _lastTimestamp ? now : _lastTimestamp + 1;
        return _lastTimestamp;
    }

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        foreach (var packet in UniversalScreen88Protocol.EncodeInitSequence(NextTimestamp))
        {
            if (!pipe.Write(packet))
            {
                return null;
            }
            DrainAck(pipe);
        }
        ServiceLog.Info($"[{HandlerId}] initialised at {UniversalScreen88Protocol.Width}x{UniversalScreen88Protocol.Height}");
        return (UniversalScreen88Protocol.Width, UniversalScreen88Protocol.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        var jpeg = _jpeg.Encode(bgra);
        var header = UniversalScreen88Protocol.EncodeFrameHeader(jpeg.Length, NextTimestamp());

        // One transfer: the 512-byte encrypted header immediately followed by the JPEG.
        var packet = new byte[header.Length + jpeg.Length];
        header.CopyTo(packet, 0);
        jpeg.CopyTo(packet.AsSpan(header.Length));
        if (!pipe.Write(packet))
        {
            return false;
        }
        DrainAck(pipe);
        return true;
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        pipe.Write(UniversalScreen88Protocol.EncodeCommand(
            UniversalScreen88Protocol.CommandStopPlay, ReadOnlySpan<byte>.Empty, NextTimestamp()));
        _jpeg.Dispose();
    }

    /// <summary>
    /// The panel answers every command, so this polls until something arrives. The reply's
    /// content carries nothing we need - only its arrival matters - so it is dropped.
    /// </summary>
    private static void DrainAck(IBulkUsbPipe pipe)
    {
        Span<byte> buffer = stackalloc byte[UniversalScreen88Protocol.PacketLength];
        for (int attempt = 0; attempt < AckAttempts; attempt++)
        {
            int read = pipe.Read(buffer, AckTimeoutMs);
            if (read < 0)
            {
                return;
            }
            if (read > 0)
            {
                return;
            }
        }
    }
}
