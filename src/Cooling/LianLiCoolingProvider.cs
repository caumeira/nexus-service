using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Lian Li Uni Hub into the fan-control subsystem.
/// Channel IDs are "lianli:port{N}" for ports 0..3.
/// </summary>
public sealed class LianLiCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly LianLiHub _hub;

    // _pendingDuty/_softwareControlled are read on HTTP threads (GetFanChannels,
    // GetAll) and mutated by the curve worker (DriveFanSpeed) plus HTTP setters;
    // guard both behind _ctrlLock like QSeriesCoolerCoolingProvider does.
    private readonly object _ctrlLock = new();
    private readonly int[] _pendingDuty = new int[LianLiProtocol.PortCount];
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);

    public LianLiCoolingProvider(LianLiHub hub)
    {
        _hub = hub;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var result = new List<FanChannel>(LianLiProtocol.PortCount);
        var deviceId = _hub.DeviceId;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            var id = $"lianli:port{p}";
            var rpm = _hub.State.Rpm[p];
            int duty;
            bool sw;
            lock (_ctrlLock)
            {
                duty = _pendingDuty[p];
                sw = _softwareControlled.Contains(id);
            }
            result.Add(new FanChannel
            {
                Id = id,
                Name = $"Uni Hub SL-Infinity Port {p}",
                DutyPercent = duty,
                Rpm = rpm >= 0 ? rpm : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = "Lian Li Uni Hub SL-Infinity",
                PortLabel = $"Port {p}",
                FanModel = null,
                Orientation = null,
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsLianLiId(channelId)) return;
        var hasPort = TryParsePort(channelId, out var port);
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            // Released to firmware control; drop the manual duty so GetAll reports Auto.
            if (hasPort) _pendingDuty[port] = 0;
        }
        if (!_hub.IsConnected) return;
        if (hasPort)
        {
            _hub.SetReleaseMode(port);
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            System.Array.Clear(_pendingDuty);
        }
        if (!_hub.IsConnected) return;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            _hub.SetReleaseMode(p);
        }
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
        var deviceId = _hub.DeviceId;
        var devices = new List<CoolingDevice>(LianLiProtocol.PortCount);
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            var rpm = _hub.State.Rpm[p];
            int duty;
            lock (_ctrlLock) duty = _pendingDuty[p];
            devices.Add(new CoolingDevice
            {
                Id = $"lianli:port{p}",
                Name = $"Uni Hub SL-Infinity Port {p}",
                Type = "Fan",
                Rpm = rpm >= 0 ? rpm : 0,
                Pwm = duty,
            });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = deviceId,
                Name = "Lian Li Uni Hub SL-Infinity",
                Type = "LianLiHub",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    public static bool IsLianLiId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli:", StringComparison.Ordinal);

    private void ApplyChannelWrite(string channelId, int dutyPercent)
    {
        if (!IsLianLiId(channelId)) return;
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[lianli-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        if (!TryParsePort(channelId, out var port)) return;
        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            _pendingDuty[port] = dutyPercent;
        }
        _hub.SetSpeed(port, dutyPercent);
    }

    private static bool TryParsePort(string channelId, out int port)
    {
        port = 0;
        // channelId shape: "lianli:port{N}"
        var portIdx = channelId.LastIndexOf(':');
        if (portIdx < 0) return false;
        var seg = channelId.AsSpan(portIdx + 1);
        if (!seg.StartsWith("port", StringComparison.Ordinal)) return false;
        if (!int.TryParse(seg.Slice(4), out port)) return false;
        // Guard the int[]/SetSpeed indexers: the curve path (DriveFanSpeed) can
        // hand a stale id like "lianli:port7" that the routes never validate.
        return port >= 0 && port < LianLiProtocol.PortCount;
    }
}
