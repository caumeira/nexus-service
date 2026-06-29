using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the Lian Li Galahad II Trinity AIO into the fan-control subsystem.
/// Channel IDs: "lianli-aio:fan" and "lianli-aio:pump".
/// </summary>
public sealed class Galahad2CoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string FanChannelId = "lianli-aio:fan";
    private const string PumpChannelId = "lianli-aio:pump";

    private readonly Galahad2Hub _hub;

    private readonly object _ctrlLock = new();
    // Tracks which channels are under software control; duties live in the hub snapshot.
    private bool _fanSw;
    private bool _pumpSw;

    public Galahad2CoolingProvider(Galahad2Hub hub)
    {
        _hub = hub;
    }

    public static bool IsGalahad2Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli-aio:", StringComparison.Ordinal);

    // IFanControlProvider

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<FanChannel>();
        }
        var snap = _hub.Snapshot;
        bool fanSw;
        bool pumpSw;
        lock (_ctrlLock)
        {
            fanSw = _fanSw;
            pumpSw = _pumpSw;
        }
        return new[]
        {
            new FanChannel
            {
                Id = FanChannelId,
                Name = "Lian Li Galahad II Fan",
                DutyPercent = fanSw ? snap.FanDuty : 0,
                Rpm = snap.FanRpm,
                Mode = fanSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Fan,
                DeviceId = "lianli-aio",
                DeviceName = "Lian Li Galahad II",
                PortLabel = "Fan",
            },
            new FanChannel
            {
                Id = PumpChannelId,
                Name = "Lian Li Galahad II Pump",
                DutyPercent = pumpSw ? snap.PumpDuty : 0,
                Rpm = snap.PumpRpm,
                Mode = pumpSw ? FanModes.Manual : FanModes.Auto,
                Kind = FanKinds.Pump,
                MinDuty = Galahad2Protocol.PumpDutyFloor,
                DeviceId = "lianli-aio",
                DeviceName = "Lian Li Galahad II",
                PortLabel = "Pump",
            },
        };
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, Galahad2Protocol.PumpDutyFloor, 100);
            ApplyPumpWrite(clamped);
            return clamped;
        }
        if (channelId == FanChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, 0, 100);
            ApplyFanWrite(clamped);
            return clamped;
        }
        return dutyPercent;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (channelId == PumpChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, Galahad2Protocol.PumpDutyFloor, 100);
            ApplyPumpWrite(clamped);
        }
        else if (channelId == FanChannelId)
        {
            int clamped = Math.Clamp(dutyPercent, 0, 100);
            ApplyFanWrite(clamped);
        }
    }

    public void ReleaseFan(string channelId)
    {
        if (!IsGalahad2Id(channelId))
        {
            return;
        }
        lock (_ctrlLock)
        {
            if (channelId == FanChannelId)
            {
                _fanSw = false;
            }
            else if (channelId == PumpChannelId)
            {
                _pumpSw = false;
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _fanSw = false;
            _pumpSw = false;
        }
    }

    // Pump duty ramps to 0 during calibration, which would stop coolant flow.
    // Neither channel is calibrated; return empty.
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ICoolingProvider

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<CoolingComponent>();
        }
        var snap = _hub.Snapshot;
        return new[]
        {
            new CoolingComponent
            {
                Id = "lianli-aio",
                Name = "Lian Li Galahad II",
                Type = "Galahad2",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice
                    {
                        Id = FanChannelId,
                        Name = "Lian Li Galahad II Fan",
                        Type = "Fan",
                        Rpm = snap.FanRpm,
                        Pwm = snap.FanDuty,
                    },
                    new CoolingDevice
                    {
                        Id = PumpChannelId,
                        Name = "Lian Li Galahad II Pump",
                        Type = "Pump",
                        Rpm = snap.PumpRpm,
                        Pwm = snap.PumpDuty,
                    },
                },
            },
        };
    }

    // Called by the connection worker after each successful RPM poll.
    public void ReassertControl()
    {
        if (!_hub.IsConnected)
        {
            return;
        }
        bool fanSw;
        bool pumpSw;
        lock (_ctrlLock)
        {
            fanSw = _fanSw;
            pumpSw = _pumpSw;
        }
        // Read snapshot once for both re-asserts so duties come from a consistent point in time.
        var snap = _hub.Snapshot;
        if (fanSw)
        {
            if (!_hub.SetFan(snap.FanDuty))
            {
                ServiceLog.Warn($"[lianli-aio-cooling] ReassertControl fan duty {snap.FanDuty} returned false");
            }
        }
        if (pumpSw)
        {
            if (!_hub.SetPump(snap.PumpDuty))
            {
                ServiceLog.Warn($"[lianli-aio-cooling] ReassertControl pump duty {snap.PumpDuty} returned false");
            }
        }
    }

    private void ApplyFanWrite(int duty)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[lianli-aio-cooling] fan write dropped: hub not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _fanSw = true;
        }
        if (!_hub.SetFan(duty))
        {
            ServiceLog.Warn($"[lianli-aio-cooling] SetFan duty {duty} returned false");
        }
    }

    private void ApplyPumpWrite(int duty)
    {
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn("[lianli-aio-cooling] pump write dropped: hub not connected");
            return;
        }
        lock (_ctrlLock)
        {
            _pumpSw = true;
        }
        if (!_hub.SetPump(duty))
        {
            ServiceLog.Warn($"[lianli-aio-cooling] SetPump duty {duty} returned false");
        }
    }
}
