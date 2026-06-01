namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Snapshot of the connected HYTE Y70 Touch display controller. Carries what
/// the Firmware Updates page needs: which variant is attached and the firmware
/// version it reports. Brightness / screen control stays with the existing
/// display-settings path.
/// </summary>
public sealed class Y70DisplayState
{
    /// <summary>"y70" / "y70-infinite" / "y70-truly" (bundled-firmware key), or empty when none connected.</summary>
    public string Variant { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form (e.g. "1.0.3.1"). Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>USB instance-id segment used as the device-id namespace. Empty until connected.</summary>
    public string Serial { get; set; } = "";
}
