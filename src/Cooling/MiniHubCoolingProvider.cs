using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Models.Cooling;
using Qos.Service.Peripherals.Hyte.MiniHub;

namespace Qos.Service.Cooling;

/// <summary>
/// Bridges the HYTE IBP MiniHub into the cooling subsystem. The MiniHub has
/// only two physical fan ports — port 1 (1 fan) and port 2 (up to 3
/// daisy-chained fans sharing one tach + one PWM). We surface one
/// <see cref="FanChannel"/> per populated port, mirroring HYTE's own
/// MinihubComponent shape. Empty ports never appear on the cooling page.
///
/// Channel IDs:
/// <code>
///   minihub:&lt;serial&gt;:port1   — port 1 PWM / tach
///   minihub:&lt;serial&gt;:port2   — port 2 PWM / tach (shared across daisy chain)
/// </code>
///
/// Both ports are set in a single firmware command, so any per-port write
/// re-sends the cached "other port" duty. The hub firmware clamps duties
/// below 10% to 0%, so a curve emitting 0% still pins to 10% — there is no
/// firmware-supported "stop" speed.
/// </summary>
public sealed class MiniHubCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly MiniHubHub _hub;

    public MiniHubCoolingProvider(MiniHubHub hub)
    {
        _hub = hub;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var state = _hub.State;
        var serial = state.Serial;
        if (string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();
        var deviceId = _hub.DeviceId;
        var result = new List<FanChannel>(2);

        if (state.Port1Fans > 0)
        {
            result.Add(new FanChannel
            {
                Id = Port1Id(serial),
                Name = "Port 1 Fan",
                DutyPercent = state.Port1Duty,
                Rpm = state.Port1Rpm,
                Mode = "Auto",
                DeviceId = deviceId,
                PortLabel = "Port 1",
            });
        }
        if (state.Port2Fans > 0)
        {
            var label = state.Port2Fans == 1 ? "Port 2 Fan" : $"Port 2 Fans ({state.Port2Fans})";
            result.Add(new FanChannel
            {
                Id = Port2Id(serial),
                Name = label,
                DutyPercent = state.Port2Duty,
                Rpm = state.Port2Rpm,
                Mode = "Auto",
                DeviceId = deviceId,
                PortLabel = "Port 2",
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        // The MiniHub firmware does not expose temperature probes — its
        // tach + PWM are all the cooling-relevant data it surfaces.
        return Array.Empty<TemperatureSource>();
    }

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyWrite(channelId, clamped);
    }

    public void ReleaseFan(string channelId)
    {
        // MiniHub control mode is hub-level, not per-port — flipping back to
        // motherboard hands BOTH ports to the MB header simultaneously. The
        // heartbeat worker will reassert software mode on the next tick if
        // any qos consumer still expects to drive a port, so this matches
        // the "release this one channel" intent for as long as no other
        // channel is actively driven.
        if (!IsMiniHubId(channelId)) return;
        if (!_hub.IsConnected) return;
        _hub.SetFanControlMode(MiniHubProtocol.FanModeMotherboard);
    }

    public void ReleaseAll()
    {
        if (!_hub.IsConnected) return;
        _hub.SetFanControlMode(MiniHubProtocol.FanModeMotherboard);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Same rationale as NP50: hub-driven fans report stable RPM at known
        // PWM and don't benefit from a calibration ramp.
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var state = _hub.State;
        var serial = state.Serial;
        if (string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>(2);
        if (state.Port1Fans > 0)
        {
            devices.Add(new CoolingDevice
            {
                Id = Port1Id(serial),
                Name = "Port 1 Fan",
                Type = "Fan",
                Rpm = state.Port1Rpm,
                Pwm = state.Port1Duty,
            });
        }
        if (state.Port2Fans > 0)
        {
            devices.Add(new CoolingDevice
            {
                Id = Port2Id(serial),
                Name = state.Port2Fans == 1 ? "Port 2 Fan" : $"Port 2 Fans ({state.Port2Fans})",
                Type = "Fan",
                Rpm = state.Port2Rpm,
                Pwm = state.Port2Duty,
            });
        }
        if (devices.Count == 0) return Array.Empty<CoolingComponent>();

        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = "HYTE IBP MiniHub",
                Type = "MiniHub",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    public static bool IsMiniHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("minihub:", StringComparison.Ordinal);

    private void ApplyWrite(string channelId, int dutyPercent)
    {
        if (!IsMiniHubId(channelId)) return;
        if (!_hub.IsConnected)
        {
            Console.Error.WriteLine($"[minihub-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        var state = _hub.State;
        var port1Target = state.Port1Duty;
        var port2Target = state.Port2Duty;
        if (channelId.EndsWith(":port1", StringComparison.Ordinal)) port1Target = dutyPercent;
        else if (channelId.EndsWith(":port2", StringComparison.Ordinal)) port2Target = dutyPercent;
        else return; // unknown sub-id; ignore rather than risk a wrong port write

        // Coerce never-driven ports to the firmware floor so we don't send
        // 0% (which the firmware would clamp anyway, but starting from a
        // clean known value keeps the heartbeat-asserted state predictable).
        if (port1Target < MiniHubProtocol.FanMinDutyPercent) port1Target = MiniHubProtocol.FanMinDutyPercent;
        if (port2Target < MiniHubProtocol.FanMinDutyPercent) port2Target = MiniHubProtocol.FanMinDutyPercent;

        _hub.SetFanControlMode(MiniHubProtocol.FanModeSoftware);
        var writeOk = _hub.WriteFanSpeed(port1Target, port2Target);
        Console.Error.WriteLine(
            $"[minihub-cooling] {channelId} -> p1={port1Target}% p2={port2Target}% (writeOk={writeOk})");
    }

    private static string Port1Id(string serial) => $"minihub:{serial}:port1";
    private static string Port2Id(string serial) => $"minihub:{serial}:port2";
}
