namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Snapshot of the connected HYTE Q-series cooler controller: which variant is
/// attached and the firmware version it reports.
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

    /// <summary>Pump-head RPM from the last Port-0 poll. 0 until first poll / when no sensor.</summary>
    public int PumpRpm { get; set; }

    /// <summary>Second-pump RPM (Q80 dual-pump). 0 on single-pump units.</summary>
    public int Pump2Rpm { get; set; }

    /// <summary>True once a Q80 second pump has reported a non-zero RPM.</summary>
    public bool HasPump2 { get; set; }

    /// <summary>Representative radiator-fan RPM (Type-M channel). 0 until first poll / when no fan.</summary>
    public int FanRpm { get; set; }

    /// <summary>True once an FT12 fan unit is reported on the Type-M channel.</summary>
    public bool HasFan { get; set; }

    /// <summary>Hub control mode byte from the last Port-0 poll (Software/Motherboard/Firmware/Mix).</summary>
    public byte ControlMode { get; set; } = QSeriesCoolerProtocol.ControlModeMotherboard;

    /// <summary>Turbo state from the last Port-0 poll.</summary>
    public bool TurboOn { get; set; }
}
