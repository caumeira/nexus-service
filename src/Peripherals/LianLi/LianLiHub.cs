using System;
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
            var ok = _device.SetFeature(LianLiProtocol.BuildManualMode(port))
                  && _device.SetFeature(LianLiProtocol.BuildSetSpeed(port, duty));
            if (ok)
            {
                State.Duty[port] = duty;
            }
            return ok;
        }
    }

    public bool SendColorData(int ch, ReadOnlySpan<byte> leds)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            LianLiProtocol.WriteColorData(_colorReport, ch, leds);
            // SET_REPORT(Output) over the control pipe; an interrupt-OUT Write
            // corrupts the Lian Li color stream.
            return _device.SetOutputReport(_colorReport);
        }
    }

    public bool SendEffectCommit(int ch)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildEffectCommit(
                ch, LianLiProtocol.EffectStatic,
                LianLiProtocol.SpeedDefault,
                LianLiProtocol.DirectionDefault,
                LianLiProtocol.BrightnessDefault));
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
