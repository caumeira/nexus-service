using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges the HYTE Q-series (Q60 / Q80) AIO pump into the cooling subsystem.
/// The primary pump head is a controllable <see cref="FanChannel"/>
/// (<see cref="FanKinds.Pump"/>): Manual drives it under software control via
/// <see cref="QSeriesCoolerHub.SetPumpSpeed"/>, BIOS hands it back to the
/// motherboard. A Q80 second pump is surfaced read-only (telemetry only).
///
/// Channel ids: <c>qseries:&lt;serial&gt;:pump</c> / <c>:pump2</c>. The hub
/// control mode is hub-wide, so only the primary pump carries the duty control.
/// </summary>
public sealed class QSeriesCoolerCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private const string IdPrefix = "qseries:";
    private const string PumpSuffix = ":pump";

    private readonly QSeriesCoolerHub _hub;

    // The pump is under software control once the user drives it; cleared on
    // BIOS/release. Tracked here because the hub has no per-channel mode flag to
    // query back — same reason as SmartHub/MiniHub. _pumpDuty is the last
    // commanded duty so GetFanChannels can report it back to the card.
    private readonly object _ctrlLock = new();
    private bool _pumpSoftware;
    private int _pumpDuty = 50;

    public QSeriesCoolerCoolingProvider(QSeriesCoolerHub hub) => _hub = hub;

    public static bool IsQSeriesId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // Only the primary pump carries a duty control; pump2 is read-only telemetry.
    private static bool IsPumpControlId(string id) =>
        IsQSeriesId(id) && id.EndsWith(PumpSuffix, StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<FanChannel>();

        bool sw; int duty;
        lock (_ctrlLock) { sw = _pumpSoftware; duty = _pumpDuty; }

        var deviceId = _hub.DeviceId;
        var deviceName = _hub.ProductName;
        var result = new List<FanChannel>(2)
        {
            new FanChannel
            {
                Id = PumpId(serial, "pump"),
                Name = "Pump",
                Kind = FanKinds.Pump,
                ReadOnly = false,
                Rpm = _hub.State.PumpRpm,
                DutyPercent = sw ? duty : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
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
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        if (!IsPumpControlId(channelId)) return clamped;
        lock (_ctrlLock) { _pumpSoftware = true; _pumpDuty = clamped; }
        _hub.SetPumpSpeed(clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) => SetFanSpeed(channelId, dutyPercent);

    public void ReleaseFan(string channelId)
    {
        if (!IsPumpControlId(channelId)) return;
        bool wasSoftware;
        lock (_ctrlLock) { wasSoftware = _pumpSoftware; _pumpSoftware = false; }
        if (wasSoftware) _hub.SetControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);
    }

    public void ReleaseAll()
    {
        bool wasSoftware;
        lock (_ctrlLock) { wasSoftware = _pumpSoftware; _pumpSoftware = false; }
        // Hand the pump back to the motherboard, matching ReleaseFan and the
        // other hub providers — ReleaseAll runs on profile switch + shutdown.
        if (wasSoftware) _hub.SetControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var serial = _hub.State.Serial;
        if (!_hub.IsConnected || string.IsNullOrEmpty(serial)) return Array.Empty<CoolingComponent>();

        var devices = new List<CoolingDevice>(2)
        {
            new CoolingDevice { Id = PumpId(serial, "pump"), Name = "Pump", Type = "Pump", Rpm = _hub.State.PumpRpm },
        };
        if (_hub.State.HasPump2)
            devices.Add(new CoolingDevice { Id = PumpId(serial, "pump2"), Name = "Pump 2", Type = "Pump", Rpm = _hub.State.Pump2Rpm });

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

    private static string PumpId(string serial, string suffix) => $"{IdPrefix}{serial}:{suffix}";
}
