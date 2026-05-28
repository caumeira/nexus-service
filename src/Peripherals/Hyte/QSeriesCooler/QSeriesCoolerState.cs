namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Snapshot of the connected HYTE Q-series cooler controller. v1 carries only
/// what the Firmware Updates page needs: which variant is attached and the
/// firmware version it reports. Pump/RGB/fan control is a follow-up.
/// </summary>
public sealed class QSeriesCoolerState
{
    /// <summary>
    /// "q60" or "q80" (the bundled-firmware directory key), or empty when no
    /// cooler is connected. Determined from the matched USB PID at connect time.
    /// </summary>
    public string Variant { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form (e.g. "2.0.9.1"). Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>USB instance-id segment used as the device-id namespace. Empty until connected.</summary>
    public string Serial { get; set; } = "";
}
