namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Firmware-version capability checks for the HYTE CNVS. The two
/// firmware settings (boot-animation suppression, keep-LEDs-on-when-PC-off)
/// are gated on a minimum firmware version - older units silently no-op the
/// FF DC 07 write.
///
/// Source: <c>hyte-refs/hyte-documents/firmware-protocol/CNVS/stm32-commands.md</c>
/// §3 - "Work with firmware update from v1.0.2.1/v1.0.2.2".
/// </summary>
public static class CnvsFirmware
{
    /// <summary>
    /// Minimum firmware version that implements the <c>FF DC 07</c> /
    /// <c>FF DC 08</c> settings command-pair. Below this, writes are
    /// accepted on the wire but silently ignored by the device.
    /// </summary>
    public static readonly (int Major, int Minor, int Build) MinSettingsVersion = (1, 0, 2);

    /// <summary>
    /// True when <paramref name="version"/> is greater-than-or-equal to the
    /// settings-feature minimum. Accepts the device's native four-part
    /// "Major.Minor.Build.Hw" format; the hardware byte is ignored for the
    /// comparison. Returns false for null / empty / unparseable input -
    /// conservative default so the UI gates the feature off when we don't
    /// yet know what's connected.
    /// </summary>
    public static bool SupportsSettings(string? version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        if (!int.TryParse(parts[1], out var minor)) return false;
        if (!int.TryParse(parts[2], out var build)) return false;
        var (m, n, b) = MinSettingsVersion;
        if (major != m) return major > m;
        if (minor != n) return minor > n;
        return build >= b;
    }
}
