using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.LianLi;

public sealed class LianLiHub : IDisposable
{
    private readonly object _lock = new();
    private readonly byte[] _colorReport = new byte[LianLiProtocol.OutputReportSize];
    private IHidDevice? _device;
    private bool _disposed;

    public string DeviceId => "lianli";

    public LianLiState State { get; } = new();

    public bool IsConnected => State.IsConnected;

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device = device;
            State.IsConnected = true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = null;
            State.IsConnected = false;
        }
    }

    public bool SetQuantity(int port, int qty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildSetQuantity(port, qty));
        }
    }

    public bool SetManualMode(int port)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildManualMode(port));
        }
    }

    public bool SetReleaseMode(int port)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildReleaseMode(port));
        }
    }

    public bool SetSpeed(int port, int duty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            // Manual-mode write, settle, then the duty write - the sequence and
            // the inter-command gap mirror uni-sync (the reference Lian Li Uni
            // controller) for PID 0xA102. The firmware drops the duty if it
            // arrives before the mode transition settles: the fan detaches from
            // mobo PWM but idles at its default, ignoring duty. Both commands go
            // interrupt-OUT; WriteFile needs the full OutputReportSize, so the
            // 7-byte commands pad into the scratch buffer.
            if (!WriteCommand(LianLiProtocol.BuildManualMode(port))) return false;
            Thread.Sleep(LianLiProtocol.FanCommandSettleMs);
            if (!WriteCommand(LianLiProtocol.BuildSetSpeed(port, duty))) return false;
            State.Duty[port] = duty;
            return true;
        }
    }

    /// <summary>
    /// Refresh the duty on a port already in manual mode - the speed write only,
    /// no manual-mode re-entry and no settle. Re-entering manual mode resets the
    /// fan to its default, so the periodic re-assert must not call <see
    /// cref="SetSpeed"/> (which re-enters): that pins the fan at its default and
    /// the duty never takes. Use this for re-assertion; use SetSpeed to first
    /// take a port off mobo PWM.
    /// </summary>
    public bool SetDuty(int port, int duty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            if (!WriteCommand(LianLiProtocol.BuildSetSpeed(port, duty))) return false;
            State.Duty[port] = duty;
            return true;
        }
    }

    // Pads a short E0 command into the OutputReportSize scratch buffer and sends
    // it interrupt-OUT. Caller holds _lock.
    private bool WriteCommand(ReadOnlySpan<byte> command)
    {
        Array.Clear(_cmdReport);
        command.CopyTo(_cmdReport);
        return _device!.Write(_cmdReport);
    }

    // Output-report scratch for the per-frame start/commit commands. Kept
    // separate from _colorReport so a command never clobbers an in-flight frame.
    private readonly byte[] _cmdReport = new byte[LianLiProtocol.OutputReportSize];

    /// <summary>
    /// Per-frame "start" announcing the port + fan count before its color push.
    /// E0 10 60 (port+1) (fans), sent as an OUTPUT report (matches OpenRGB
    /// SendStartAction). The firmware applies the streamed colors per frame only
    /// when each push is framed by this start; without it the panel re-renders on
    /// its own slow internal cadence (~0.6 Hz) regardless of stream rate.
    /// </summary>
    public bool SendStartAction(int port, int fans)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            Array.Clear(_cmdReport);
            _cmdReport[0] = LianLiProtocol.ReportId;
            _cmdReport[1] = 0x10;
            _cmdReport[2] = 0x60;
            _cmdReport[3] = (byte)(port + 1);
            _cmdReport[4] = (byte)Math.Clamp(fans, 0, LianLiProtocol.MaxFansPerPort);
            return _device.Write(_cmdReport);
        }
    }

    public bool SendColorData(int ch, ReadOnlySpan<byte> leds)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            LianLiProtocol.WriteColorData(_colorReport, ch, leds);
            // Interrupt-OUT Write (matches OpenRGB hid_write). The firmware applies
            // each interrupt frame promptly; the control-pipe SetOutputReport path
            // is accepted but only repainted on the slow internal cadence. Requires
            // the per-write settle in the writer - without it the stream corrupts.
            return _device.Write(_colorReport);
        }
    }

    /// <summary>
    /// Per-channel commit applying the just-streamed colors (STATIC_COLOR). Sent
    /// as an OUTPUT report (matches OpenRGB SendCommitAction); a feature-report
    /// commit applies, but only on the firmware's slow internal cadence.
    /// </summary>
    public bool SendEffectCommit(int ch) =>
        SendModeCommit(ch, LianLiProtocol.EffectStatic, LianLiProtocol.SpeedDefault,
            LianLiProtocol.DirectionDefault, LianLiProtocol.BrightnessDefault);

    /// <summary>
    /// Commit a firmware effect on channel ch. E0 (0x10|ch) effect speed dir
    /// brightness, OUTPUT report. Firmware-generated modes (rainbow, breathing,
    /// etc.) animate on-chip from this single commit - no per-LED streaming.
    /// </summary>
    public bool SendModeCommit(int ch, byte effect, byte speed, byte dir, byte brightness)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return WriteCommand(LianLiProtocol.BuildEffectCommit(ch, effect, speed, dir, brightness));
        }
    }

    public bool SendFrameLatch()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildFrameLatch());
        }
    }

    public bool ReadRpm()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            if (!_device.SetFeature(LianLiProtocol.BuildRpmPrimer())) return false;
            Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
            buf[0] = LianLiProtocol.ReportId;
            if (!_device.GetInputReport(buf)) return false;
            for (var i = 0; i < LianLiProtocol.PortCount; i++)
            {
                var rpm = LianLiProtocol.DecodeRpm(buf, i);
                if (rpm >= 0)
                {
                    State.Rpm[i] = rpm;
                }
            }
            return true;
        }
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
