namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Mutable snapshot of the connected Keeb TKL. POCO; reads are eventually
/// consistent (fine for the panel / lighting providers). Owned by
/// <see cref="KeebHub"/>.
/// </summary>
public sealed class KeebState
{
    /// <summary>HID serial (or device-path hash fallback) - stable per physical unit.</summary>
    public string Serial { get; set; } = "";

    /// <summary>"Major.Minor" firmware version, empty until the device-info read succeeds.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>"ANSI" or "ISO". Defaults ANSI until device-info is read.</summary>
    public string Layout { get; set; } = "ANSI";

    /// <summary>Active firmware profile index (0 or 1).</summary>
    public int Profile { get; set; }
}
