namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Top-level snapshot of a HYTE Smart Hub. The hub has four ARGB ports and
/// four PWM-fan ports. The firmware does NOT enumerate how many LEDs are
/// wired to each ARGB port (same as the MiniHub) — the user declares the
/// per-port LED count, which we persist in <c>settings.Devices.ZoneLedCounts</c>
/// and surface as resizable lighting zones. The PWM ports DO report a tach
/// reading + an enabled flag, which the heartbeat refreshes each tick.
/// </summary>
public sealed class SmartHubState
{
    /// <summary>USB device instance-id segment (serial). Used as the device-id namespace (<c>smarthub:&lt;serial&gt;</c>).</summary>
    public string Serial { get; set; } = "";

    /// <summary>Firmware version in "Major.Minor.Build.Hw" form. Empty until first poll.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>
    /// The four ARGB ports (streaming channels 1..4). LedCount defaults to 0:
    /// a generic ARGB hub can't know what the user wired, so each port starts
    /// dark and the user declares the count via the lighting page (the zone is
    /// resizable up to <see cref="SmartHubProtocol.MaxLedsPerPort"/>).
    /// </summary>
    public SmartHubLedZone[] Ports { get; } =
    {
        new() { Channel = 1, LedCount = 0 },
        new() { Channel = 2, LedCount = 0 },
        new() { Channel = 3, LedCount = 0 },
        new() { Channel = 4, LedCount = 0 },
    };

    /// <summary>The four PWM-fan channels (wire index 0..3).</summary>
    public SmartHubFanChannel[] Fans { get; } =
    {
        new() { Index = 0 },
        new() { Index = 1 },
        new() { Index = 2 },
        new() { Index = 3 },
    };
}

/// <summary>One Smart Hub ARGB port. The user adjusts LedCount to match the strip / fan rings they wired.</summary>
public sealed class SmartHubLedZone
{
    /// <summary>Streaming channel byte (1..4, matching the physical ARGB port number).</summary>
    public int Channel { get; set; }

    /// <summary>Number of LEDs the user declared on this port.</summary>
    public int LedCount { get; set; }
}

/// <summary>One Smart Hub PWM-fan port — live tach + last-commanded duty + firmware-reported enabled flag.</summary>
public sealed class SmartHubFanChannel
{
    /// <summary>Wire channel index (0..3).</summary>
    public int Index { get; set; }

    /// <summary>Last polled tach reading in RPM. 0 when the port is empty or the fan is stopped.</summary>
    public int Rpm { get; set; }

    /// <summary>Last commanded duty (0..100%). 0 until driven from software.</summary>
    public int Duty { get; set; }

    /// <summary>
    /// Firmware-reported "port output enabled" flag from the last poll.
    /// Bookkeeping only — fw 1.0.0.1 reports 0x01 for every port regardless
    /// of fan presence, so this must NOT drive presence.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Latched true once the port shows a live tach this connection — the only
    /// real presence signal this firmware gives. Keeps a fan the user parks at
    /// 0% from vanishing off the cooling page. Cleared on disconnect.
    /// </summary>
    public bool SeenFan { get; set; }
}
