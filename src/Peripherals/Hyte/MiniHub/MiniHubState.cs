namespace Qos.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Top-level snapshot of a HYTE IBP MiniHub. All four physical ports
/// can carry LEDs:
///
///   • Port 1: 1 RGB fan via Nexus-Link (motor PWM + ring on the same
///     cable). Streamed on channel 1. Default 16 LEDs (one fan ring).
///   • Port 2: up to 3 RGB fans, streamed on channel 2. Default 48
///     LEDs (3 fan rings).
///   • Port 3: LED-strip output, streamed on channel 3. Default 16
///     LEDs per the spec table.
///   • Port 4: LED-strip output, streamed on channel 4. Default 0
///     LEDs — user must declare how many they wired.
///
/// The MiniHub firmware does NOT enumerate connected hardware — the
/// official HYTE tool keeps these counts in a user-edited config
/// (MiniHubLayoutConfig) and we mirror that via
/// settings.Devices.ZoneLedCounts overrides exposed on the lighting page.
/// </summary>
public sealed class MiniHubState
{
    /// <summary>USB device instance id segment (e.g. "205D36703632"). Used as the device-id namespace.</summary>
    public string Serial { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form. Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    public MiniHubLedZone Port1 { get; set; } = new() { Channel = 1, LedCount = 16 };
    public MiniHubLedZone Port2 { get; set; } = new() { Channel = 2, LedCount = 48 };
    public MiniHubLedZone Port3 { get; set; } = new() { Channel = 3, LedCount = 16 };
    public MiniHubLedZone Port4 { get; set; } = new() { Channel = 4, LedCount = 0 };
}

/// <summary>One MiniHub LED port — the user can adjust LedCount if the strip they wired differs from the firmware default.</summary>
public sealed class MiniHubLedZone
{
    /// <summary>Streaming channel byte (1..4, matching the physical port number).</summary>
    public int Channel { get; set; }

    /// <summary>Number of LEDs on the wired strip / fan ring(s).</summary>
    public int LedCount { get; set; }
}
