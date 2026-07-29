using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Corsair iCUE LINK System Hub into the fan-control subsystem.
/// Channel IDs are "corsair:ch{N}" where N is the 1-based daisy-chain position
/// of a speed-capable device. Temperature probes (QX fans, liquid loops, pump
/// blocks) are exposed as curve-input sources "corsair:ch{N}:temp".
///
/// The hub has no motherboard-PWM fallback: in software mode every speed channel
/// is driven by the host or it runs the hub's default. So uncontrolled channels
/// are held at a safe default duty each poll rather than "released to BIOS".
/// </summary>
public sealed class CorsairLinkCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const int DefaultFanDuty = 50;
    private const int DefaultPumpDuty = 70;
    // Pump/AIO min-duty floors mirror OpenLinkHub (lsh.go): a manual write floors
    // any pump at 50; a curve write floors a standard AIO at 70, a Titan AIO at 31,
    // and a standalone pump (XD5/XD6 etc.) at 30. Fans are never floored.
    private const int PumpManualFloor = 50;
    private const int AioCurveFloor = 70;
    private const int TitanAioCurveFloor = 31;
    private const int PumpCurveFloor = 30;
    private const int TitanAioType = 17;

    private readonly CorsairLinkHub _hub;

    private readonly object _ctrlLock = new();
    private readonly Dictionary<int, int> _pendingDuty = new();
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);

    public CorsairLinkCoolingProvider(CorsairLinkHub hub)
    {
        _hub = hub;
    }

    public static bool IsCorsairId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("corsair:", StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var deviceId = _hub.DeviceId;
        var result = new List<FanChannel>();
        foreach (var d in _hub.State.Devices)
        {
            if (!d.HasSpeed) continue;
            var id = $"corsair:ch{d.Channel}";
            bool sw;
            int duty;
            lock (_ctrlLock)
            {
                sw = _softwareControlled.Contains(id);
                duty = _pendingDuty.TryGetValue(d.Channel, out var p) ? p : DefaultDutyFor(d);
            }
            var isPump = d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio;
            result.Add(new FanChannel
            {
                Id = id,
                Name = d.Name,
                DutyPercent = duty,
                Rpm = d.Rpm >= 0 ? d.Rpm : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
                Kind = isPump ? FanKinds.Pump : FanKinds.Fan,
                DeviceId = deviceId,
                DeviceName = "Corsair iCUE LINK",
                PortLabel = $"Fan {d.Channel}",
                FanModel = d.Name,
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (!_hub.IsConnected) return Array.Empty<TemperatureSource>();
        var deviceId = _hub.DeviceId;
        var result = new List<TemperatureSource>();
        foreach (var d in _hub.State.Devices)
        {
            if (!d.HasTemperature || float.IsNaN(d.TempC)) continue;
            result.Add(new TemperatureSource
            {
                Id = $"corsair:ch{d.Channel}:temp",
                Name = $"{d.Name} probe (Fan {d.Channel})",
                Category = "Hub",
                Value = d.TempC,
                DeviceId = deviceId,
                DeviceName = d.Name,
            });
        }
        return result;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!IsCorsairId(sensorId) || !_hub.IsConnected) return null;
        if (!TryParseChannel(sensorId, out var ch)) return null;
        foreach (var d in _hub.State.Devices)
        {
            if (d.Channel != ch) continue;
            return d.HasTemperature && !float.IsNaN(d.TempC) ? d.TempC : null;
        }
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = ApplyPumpFloor(channelId, Math.Clamp(dutyPercent, 0, 100), curve: false);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        ApplyChannelWrite(channelId, ApplyPumpFloor(channelId, Math.Clamp(dutyPercent, 0, 100), curve: true));
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsCorsairId(channelId)) return;
        if (TryParseChannel(channelId, out var ch))
        {
            lock (_ctrlLock)
            {
                _softwareControlled.Remove(channelId);
                _pendingDuty.Remove(ch);
            }
        }
        PushAll();
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            _pendingDuty.Clear();
        }
        PushAll();
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var devices = new List<CoolingDevice>();
        foreach (var d in _hub.State.Devices)
        {
            if (!d.HasSpeed) continue;
            int duty;
            lock (_ctrlLock) duty = _pendingDuty.TryGetValue(d.Channel, out var p) ? p : DefaultDutyFor(d);
            devices.Add(new CoolingDevice
            {
                Id = $"corsair:ch{d.Channel}",
                Name = d.Name,
                Type = d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio ? "Pump" : "Fan",
                Rpm = d.Rpm >= 0 ? d.Rpm : 0,
                Temperature = float.IsNaN(d.TempC) ? null : d.TempC,
                Pwm = duty,
            });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = "Corsair iCUE LINK",
                Type = "CorsairLink",
                Devices = devices,
            },
        };
    }

    /// <summary>
    /// Re-push every speed channel's duty: software-controlled channels hold their
    /// commanded duty, the rest are held at a safe default. Called each poll so a
    /// fan never drifts to the hub's software-mode default after takeover.
    /// </summary>
    public void ReassertControl()
    {
        if (!_hub.IsConnected) return;
        PushAll();
    }

    // ── internals ──

    private void ApplyChannelWrite(string channelId, int dutyPercent)
    {
        if (!IsCorsairId(channelId)) return;
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[corsair-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        if (!TryParseChannel(channelId, out var ch)) return;
        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            _pendingDuty[ch] = dutyPercent;
        }
        PushAll();
    }

    // Build one duty packet for every speed-capable channel and send it. The hub
    // sets all channels in a single write, so there is no per-channel re-entry.
    private void PushAll()
    {
        if (!_hub.IsConnected) return;
        var items = new List<(int channel, int duty)>();
        lock (_ctrlLock)
        {
            foreach (var d in _hub.State.Devices)
            {
                if (!d.HasSpeed) continue;
                var duty = _pendingDuty.TryGetValue(d.Channel, out var p) ? p : DefaultDutyFor(d);
                items.Add((d.Channel, duty));
            }
        }
        if (items.Count == 0) return;
        if (!_hub.SetDuties(items))
        {
            ServiceLog.Warn("[corsair-cooling] SetDuties returned false");
        }
    }

    private static int DefaultDutyFor(CorsairLinkDevice d) =>
        d.Class is CorsairLinkClass.Pump or CorsairLinkClass.Aio ? DefaultPumpDuty : DefaultFanDuty;

    // Raise a pump/AIO duty to its safe minimum so a liquid cooler never runs too
    // slow. Fans pass through unchanged. curve=false is a manual user write.
    private int ApplyPumpFloor(string channelId, int duty, bool curve)
    {
        if (!_hub.IsConnected || !TryParseChannel(channelId, out var ch)) return duty;
        foreach (var d in _hub.State.Devices)
        {
            if (d.Channel != ch) continue;
            if (d.Class is not (CorsairLinkClass.Pump or CorsairLinkClass.Aio)) return duty;
            var floor = !curve ? PumpManualFloor
                : d.Class == CorsairLinkClass.Aio ? (d.Type == TitanAioType ? TitanAioCurveFloor : AioCurveFloor)
                : PumpCurveFloor;
            return Math.Max(duty, floor);
        }
        return duty;
    }

    private static bool TryParseChannel(string channelId, out int channel)
    {
        channel = 0;
        // shape: "corsair:ch{N}" or "corsair:ch{N}:temp"
        var span = channelId.AsSpan();
        var chIdx = span.IndexOf("ch", StringComparison.Ordinal);
        if (chIdx < 0) return false;
        var rest = span.Slice(chIdx + 2);
        var end = rest.IndexOf(':');
        if (end >= 0) rest = rest.Slice(0, end);
        return int.TryParse(rest, out channel) && channel > 0;
    }
}
