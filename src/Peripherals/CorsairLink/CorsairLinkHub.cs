using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Owns the iCUE LINK System Hub HID device and serializes all access. Holds the
/// hub in software/direct mode for its connected lifetime (handing control back
/// to firmware on detach), enumerates the daisy chain, reads speed/temperature
/// telemetry, sets fan duty, and streams per-LED color. Every public operation
/// runs the full close->open->io->close endpoint sequence under <see cref="_lock"/>.
/// </summary>
public sealed class CorsairLinkHub : IDisposable
{
    private const int ReadTimeoutMs = 250;

    private readonly object _lock = new();
    private readonly byte[] _write = new byte[CorsairLinkProtocol.WriteBufferLength];
    // _readRaw receives the full interrupt-IN report (report-id byte at [0]); _read
    // holds it stripped of that byte, the layout the parsers are calibrated to.
    private readonly byte[] _readRaw = new byte[CorsairLinkProtocol.WriteBufferLength];
    private readonly byte[] _read = new byte[CorsairLinkProtocol.ReportLength];
    // Color stream scratch: 6-byte inner header + the concatenated RGB. 8192 caps
    // the chain at ~2728 LEDs, far above a full 24-device chain; SendColors drops
    // a frame that would overflow it rather than corrupt the stream.
    private readonly byte[] _colorInner = new byte[8192];

    private IHidDevice? _device;
    private bool _softwareMode;
    private bool _colorOpen;
    private bool _disposed;

    public string DeviceId => "corsair";

    public CorsairLinkState State { get; } = new();

    public bool IsConnected => State.IsConnected;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device = device;
            _softwareMode = false;
            _colorOpen = false;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_device != null && _softwareMode)
            {
                // Hand the chain back to firmware so the fans keep running on the
                // hub's own curve once Nexus lets go.
                Transfer(CorsairLinkProtocol.CmdHardwareMode);
            }
            _device?.Dispose();
            _device = null;
            State.IsConnected = false;
            State.Firmware = "";
            State.Devices = Array.Empty<CorsairLinkDevice>();
            _softwareMode = false;
            _colorOpen = false;
        }
    }

    /// <summary>
    /// Enter software mode, read firmware, enumerate the chain, and open the color
    /// endpoint. Marks the hub connected on success. Returns false if the device
    /// stops responding mid-init.
    /// </summary>
    public bool Initialize()
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var fw = Transfer(CorsairLinkProtocol.CmdGetFirmware);
            ServiceLog.Info($"[corsair-diag] fw transfer n={fw} head={DiagHex(_read, 12)}");
            if (fw >= 8)
            {
                State.Firmware = $"{_read[4]}.{_read[5]}.{_read[6] | (_read[7] << 8)}";
            }

            Transfer(CorsairLinkProtocol.CmdSoftwareMode);
            // Firmware needs ~500 ms after entering software mode before it accepts
            // further commands (OpenLinkHub transferTimeout, lsh.go:4886).
            Thread.Sleep(CorsairLinkProtocol.SoftwareModeSettleMs);
            _softwareMode = true;

            if (!RefreshLocked()) return false;

            // The color endpoint is opened once and stays open for the connected
            // lifetime; the firmware keeps it open while Poll/SetDuties cycle the
            // data endpoints (0x36/0x17/0x21/0x18) under the same lock, matching
            // OpenLinkHub's setColorEndpoint-once design.
            Span<byte> mode = stackalloc byte[] { CorsairLinkProtocol.ModeSetColor };
            Transfer(CorsairLinkProtocol.CmdCloseEndpoint, mode);
            Transfer(CorsairLinkProtocol.CmdOpenColorEndpoint, mode);
            _colorOpen = true;

            State.IsConnected = true;
            return true;
        }
    }

    /// <summary>Re-enumerate the chain and refresh speed/temperature telemetry. Catches hot-plug.</summary>
    public bool Poll()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return RefreshLocked();
        }
    }

    /// <summary>Set duty percent for the given channels in one packet. Retries on rejection.</summary>
    public bool SetDuties(IReadOnlyList<(int channel, int duty)> items)
    {
        lock (_lock)
        {
            if (_device == null || items.Count == 0) return false;
            var count = Math.Min(items.Count, CorsairLinkProtocol.MaxChannels);
            Span<byte> payload = stackalloc byte[1 + CorsairLinkProtocol.MaxChannels * 4];
            payload[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var (channel, duty) = items[i];
                var clamped = Math.Clamp(duty, 0, 100);
                var o = 1 + i * 4;
                payload[o] = (byte)channel;
                payload[o + 1] = 0x00;
                payload[o + 2] = (byte)clamped;
                payload[o + 3] = 0x00;
            }
            return WriteEndpointLocked(CorsairLinkProtocol.ModeSetSpeed,
                CorsairLinkProtocol.DataSetSpeed, payload.Slice(0, 1 + count * 4),
                retries: CorsairLinkProtocol.SpeedSetRetries);
        }
    }

    /// <summary>
    /// Stream one frame of per-LED color. <paramref name="rgb"/> is every RGB
    /// device's LEDs concatenated in channel order, 3 bytes (R,G,B) each. The
    /// color endpoint must already be open (Initialize did this).
    /// </summary>
    public bool SendColors(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_device == null || !_colorOpen) return false;
            var len = 6 + rgb.Length;
            if (len > _colorInner.Length) return false;

            var bodyLen = rgb.Length + 2;
            _colorInner[0] = (byte)(bodyLen & 0xFF);
            _colorInner[1] = (byte)((bodyLen >> 8) & 0xFF);
            _colorInner[2] = 0x00;
            _colorInner[3] = 0x00;
            _colorInner[4] = CorsairLinkProtocol.DataSetColor[0];
            _colorInner[5] = CorsairLinkProtocol.DataSetColor[1];
            rgb.CopyTo(_colorInner.AsSpan(6));

            var offset = 0;
            var first = true;
            while (offset < len)
            {
                var chunk = Math.Min(CorsairLinkProtocol.MaxColorChunk, len - offset);
                var cmd = first ? CorsairLinkProtocol.CmdWriteColor : CorsairLinkProtocol.CmdWriteSubColor;
                if (Transfer(cmd, _colorInner.AsSpan(offset, chunk)) <= 0) return false;
                offset += chunk;
                first = false;
            }
            return true;
        }
    }

    // ── internals (caller holds _lock) ──

    private bool RefreshLocked()
    {
        var devResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetDevices);
        if (devResp == null) return false;
        var discovered = CorsairLinkProtocol.ParseDevices(devResp);

        var speeds = new int[CorsairLinkProtocol.SensorArrayLength];
        var temps = new float[CorsairLinkProtocol.SensorArrayLength];
        Array.Fill(speeds, -1);
        Array.Fill(temps, float.NaN);

        var spResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetSpeeds);
        if (spResp != null) CorsairLinkProtocol.ParseSpeeds(spResp, speeds);
        var tpResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetTemperatures);
        if (tpResp != null) CorsairLinkProtocol.ParseTemperatures(tpResp, temps);

        var list = new List<CorsairLinkDevice>(discovered.Count);
        foreach (var d in discovered)
        {
            var meta = CorsairLinkModels.Lookup(d.Type, d.Model);
            var dev = new CorsairLinkDevice
            {
                Channel = d.Channel,
                Type = d.Type,
                Model = d.Model,
                Name = meta.Name,
                Class = meta.Class,
                LedCount = meta.LedCount,
                HasSpeed = meta.HasSpeed,
                HasTemperature = meta.HasTemperature,
                Serial = d.Serial,
                Rpm = d.Channel < speeds.Length ? speeds[d.Channel] : -1,
                TempC = d.Channel < temps.Length ? temps[d.Channel] : float.NaN,
            };
            list.Add(dev);
        }
        State.Devices = list;
        return true;
    }

    // close -> open -> read one endpoint; returns a copy of the response (the
    // shared _read buffer is clobbered by the trailing close), or null on failure.
    private byte[]? ReadEndpointLocked(byte mode)
    {
        Span<byte> m = stackalloc byte[] { mode };
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        Transfer(CorsairLinkProtocol.CmdOpenEndpoint, m);
        var n = Transfer(CorsairLinkProtocol.CmdRead, m);
        byte[]? copy = null;
        if (n > 0)
        {
            copy = _read.AsSpan(0, CorsairLinkProtocol.ReportLength).ToArray();
        }
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        return copy;
    }

    private static string DiagHex(byte[] b, int count)
    {
        var n = Math.Min(count, b.Length);
        var sb = new System.Text.StringBuilder(n * 3);
        for (var i = 0; i < n; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    // close -> open -> write(inner) -> close. Inner = [len_lo, len_hi, 0, 0,
    // dataType(2), data]. Success when response[3]==0; retry the write only.
    private bool WriteEndpointLocked(byte mode, ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data, int retries)
    {
        Span<byte> m = stackalloc byte[] { mode };
        var len = 6 + data.Length;
        Span<byte> inner = stackalloc byte[1 + CorsairLinkProtocol.MaxChannels * 4 + 6];
        if (len > inner.Length) return false;
        var bodyLen = data.Length + 2;
        inner[0] = (byte)(bodyLen & 0xFF);
        inner[1] = (byte)((bodyLen >> 8) & 0xFF);
        inner[2] = 0x00;
        inner[3] = 0x00;
        inner[4] = dataType[0];
        inner[5] = dataType[1];
        data.CopyTo(inner.Slice(6));
        var frame = inner.Slice(0, len);

        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        Transfer(CorsairLinkProtocol.CmdOpenEndpoint, m);
        var ok = false;
        var attempts = 0;
        for (var attempt = 0; attempt < Math.Max(1, retries); attempt++)
        {
            attempts++;
            var n = Transfer(CorsairLinkProtocol.CmdWrite, frame);
            if (n >= 4 && _read[3] == 0x00)
            {
                ok = true;
                break;
            }
            Thread.Sleep(CorsairLinkProtocol.SpeedSetRetryDelayMs);
        }
        ServiceLog.Info($"[corsair-diag] write 0x{mode:X2} ok={ok} attempts={attempts} inHead={DiagHex(_write, 24)} respHead={DiagHex(_read, 8)}");
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        return ok;
    }

    private int Transfer(ReadOnlySpan<byte> command) => Transfer(command, default);

    private int Transfer(ReadOnlySpan<byte> command, ReadOnlySpan<byte> payload)
    {
        if (_device == null) return -1;
        Array.Clear(_write);
        _write[1] = 0x00;
        _write[2] = 0x01;
        var off = CorsairLinkProtocol.HeaderSize;
        command.CopyTo(_write.AsSpan(off));
        off += command.Length;
        if (!payload.IsEmpty) payload.CopyTo(_write.AsSpan(off));
        if (!_device.Write(_write)) return -1;
        var n = _device.Read(_readRaw, ReadTimeoutMs);
        if (n <= 0) return n;
        // Windows ReadFile returns the report with the report-id byte at [0]; hidapi
        // (which the parsers mirror) drops it. Strip it and report the stripped length.
        _readRaw.AsSpan(1, CorsairLinkProtocol.ReportLength).CopyTo(_read);
        return n - 1;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _device?.Dispose();
            _device = null;
        }
    }
}
