using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the HYTE Q-series (Q60 / Q80) AIO into the cooling subsystem. The pump
/// head and the radiator fans are both controllable <see cref="FanChannel"/>s:
/// Manual / Curve drive them under software control, BIOS hands them to the
/// motherboard, FW Control runs the onboard firmware curve.
///
/// The cooler has a SINGLE hub-wide control mode shared by the pump and fans, so
/// the mode (software / motherboard / firmware) is shared while each channel keeps
/// its own duty. <see cref="QSeriesCoolerHub.DesiredControlMode"/> is the pinned
/// mode the engine must not override: while it is a non-software mode, duty writes
/// are swallowed so the engine doesn't flip a user-chosen BIOS/FW mode back to
/// software. Mirrors <see cref="Np50CoolingProvider"/>. A Q80 second pump is
/// surfaced read-only. Channel ids: <c>qseries:&lt;serial&gt;:pump</c> / <c>:pump2</c> / <c>:fans</c>;
/// coolant sensor ids: <c>:coolant-in</c> / <c>:coolant-out</c>.
/// </summary>
public sealed class QSeriesCoolerCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "qseries:";
    private const string PumpSuffix = ":pump";
    private const string FanSuffix = ":fans";

    private readonly QSeriesCoolerHub _hub;

    // Channels under active software control (Manual/Curve). The pump and fans
    // share the hub's one mode, so the hub stays in software while any channel is
    // driven and reverts to motherboard once all are released. _pumpDuty/_fanDuty
    // are the last commanded duties so GetFanChannels can report them back.
    private readonly object _ctrlLock = new();
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);
    private int _pumpDuty = 50;
    private int _fanDuty = 50;

    public QSeriesCoolerCoolingProvider(QSeriesCoolerHub hub) => _hub = hub;

    public static bool IsQSeriesId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ":pump" excludes ":pump2" (EndsWith); ":fans" is the radiator-fan channel.
    private static bool IsPumpControlId(string id) =>
        IsQSeriesId(id) && id.EndsWith(PumpSuffix, StringComparison.Ordinal);
    private static bool IsFanControlId(string id) =>
        IsQSeriesId(id) && id.EndsWith(FanSuffix, StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();

        var pumpId = PumpId(serial, "pump");
        var fanId = PumpId(serial, "fans");
        bool pumpSw, fanSw; int pumpDuty, fanDuty;
        lock (_ctrlLock)
        {
            pumpSw = _softwareControlled.Contains(pumpId);
            fanSw = _softwareControlled.Contains(fanId);
            pumpDuty = _pumpDuty;
            fanDuty = _fanDuty;
        }

        var deviceId = _hub.DeviceId;
        var deviceName = _hub.ProductName;
        var result = new List<FanChannel>(3)
        {
            new FanChannel
            {
                Id = pumpId,
                Name = "Pump",
                Kind = FanKinds.Pump,
                ReadOnly = false,
                Rpm = _hub.State.PumpRpm,
                DutyPercent = pumpSw ? pumpDuty : 0,
                Mode = pumpSw ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceName,
                PortLabel = "Pump",
            },
        };
        if (_hub.State.HasPump2)
        {
            result.Add(new FanChannel
            {
                Id = PumpId(serial, "pump2"),
                Name = "Pump 2",
                Kind = FanKinds.Pump,
                ReadOnly = true,
                Rpm = _hub.State.Pump2Rpm,
                Mode = FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceName,
                PortLabel = "Pump 2",
            });
        }
        if (_hub.State.HasFan)
        {
            result.Add(new FanChannel
            {
                Id = fanId,
                Name = "Fans",
                Kind = FanKinds.Fan,
                ReadOnly = false,
                Rpm = _hub.State.FanRpm,
                DutyPercent = fanSw ? fanDuty : 0,
                Mode = fanSw ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceName,
                PortLabel = "Radiator fans",
            });
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<TemperatureSource>();

        // A probeless input saturates the thermistor table, which the decode already drops.
        var result = new List<TemperatureSource>(2);
        if (_hub.State.CoolantTempInC is { } inC)
            result.Add(CoolantSource(serial, "coolant-in", "Coolant in", inC));
        if (_hub.State.CoolantTempOutC is { } outC)
            result.Add(CoolantSource(serial, "coolant-out", "Coolant out", outC));
        return result;
    }

    private TemperatureSource CoolantSource(string serial, string suffix, string name, float value) => new()
    {
        Id = PumpId(serial, suffix),
        Name = name,
        Category = "Cooler",
        Value = value,
        DeviceId = _hub.DeviceId,
        DeviceName = _hub.ProductName,
    };

    public float? ReadTemperature(string sensorId)
    {
        if (!IsQSeriesId(sensorId)) return null;
        foreach (var s in GetTemperatureSources())
            if (s.Id == sensorId) return s.Value;
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) =>
        ApplyChannelWrite(channelId, Math.Clamp(dutyPercent, 0, 100));

    public void ReleaseFan(string channelId)
    {
        if (!IsPumpControlId(channelId) && !IsFanControlId(channelId)) return;
        bool empty;
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            empty = _softwareControlled.Count == 0;
        }
        // Hub mode is shared: only hand back to the motherboard once BOTH the pump
        // and fans are released, so releasing one doesn't yank PWM from the other.
        if (empty) _hub.SetControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);
    }

    public void ReleaseAll()
    {
        bool any;
        lock (_ctrlLock)
        {
            any = _softwareControlled.Count > 0;
            _softwareControlled.Clear();
        }
        if (any) _hub.SetControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>(3)
        {
            new CoolingDevice
            {
                Id = PumpId(serial, "pump"),
                Name = "Pump",
                Type = "Pump",
                Rpm = _hub.State.PumpRpm,
                Temperature = _hub.State.CoolantTempInC,
                PumpTempIn = _hub.State.CoolantTempInC,
                PumpTempOut = _hub.State.CoolantTempOutC,
            },
        };
        if (_hub.State.HasPump2)
            devices.Add(new CoolingDevice { Id = PumpId(serial, "pump2"), Name = "Pump 2", Type = "Pump", Rpm = _hub.State.Pump2Rpm });
        if (_hub.State.HasFan)
            devices.Add(new CoolingDevice { Id = PumpId(serial, "fans"), Name = "Fans", Type = "Fan", Rpm = _hub.State.FanRpm });

        return new[]
        {
            new CoolingComponent
            {
                Id = _hub.DeviceId,
                Name = _hub.ProductName,
                Type = _hub.Variant == QSeriesCoolerProtocol.VariantQ80 ? "Q80" : "Q60",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    private void ApplyChannelWrite(string channelId, int duty)
    {
        var isPump = IsPumpControlId(channelId);
        var isFan = IsFanControlId(channelId);
        if (!isPump && !isFan) return;
        if (!_hub.IsConnected) return;

        // User pinned a non-software mode (BIOS = motherboard, FW Control =
        // firmware): swallow the duty write so the engine's next tick doesn't
        // flip the shared pump+fan hub back to software. Drop the channel from
        // the software-control set so GetFanChannels reports its pinned state.
        if (_hub.DesiredControlMode is byte pinned && pinned != QSeriesCoolerProtocol.ControlModeSoftware)
        {
            lock (_ctrlLock) _softwareControlled.Remove(channelId);
            return;
        }

        lock (_ctrlLock)
        {
            _softwareControlled.Add(channelId);
            if (isPump) _pumpDuty = duty; else _fanDuty = duty;
        }
        // Driving implies software control; latch it so a sibling channel's write
        // (or this one's next tick) isn't swallowed.
        _hub.MarkDesiredControlMode(QSeriesCoolerProtocol.ControlModeSoftware);
        if (isPump) _hub.SetPumpSpeed(duty);
        else _hub.SetFanSpeed(duty);
    }

    private static string PumpId(string serial, string suffix) => $"{IdPrefix}{serial}:{suffix}";
}
