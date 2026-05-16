namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Top-level snapshot of a HYTE IBP MiniHub's last known state. The
/// MiniHub has 4 physical ports: ports 1 + 2 carry fans (1 and up to 3
/// respectively), ports 3 + 4 carry addressable LED strips. v1 focuses on
/// LED streaming, so fan-side fields are read-only / future work.
/// </summary>
public sealed class MiniHubState
{
    /// <summary>USB device instance id segment (e.g. "205D36703632"). Used as the device-id namespace.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form. Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>LED zones the hub exposes. Two on the spec: port-3 strip (up to ~100 LEDs) + port-4 strip (often unpopulated).</summary>
    public MiniHubLedZone Port3 { get; set; } = new() { Channel = 1, LedCount = 16 };
    public MiniHubLedZone Port4 { get; set; } = new() { Channel = 2, LedCount = 0 };
}

/// <summary>One MiniHub LED port — the user can adjust LedCount if the strip they wired differs from the firmware default.</summary>
public sealed class MiniHubLedZone
{
    /// <summary>Wire-level channel byte the streaming command addresses (1 = port 3, 2 = port 4 per the spec table).</summary>
    public int Channel { get; set; }

    /// <summary>Number of LEDs on the wired strip.</summary>
    public int LedCount { get; set; }
}
