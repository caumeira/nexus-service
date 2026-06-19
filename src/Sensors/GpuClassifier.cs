namespace Nexus.Service.Sensors;

/// <summary>
/// Best-effort vendor + integrated/discrete guess from a GPU model string, for
/// platforms that expose only a name (macOS system_profiler, Linux lspci).
/// Windows classifies from the LibreHardwareMonitor HardwareType instead, which
/// is authoritative. The integrated flag drives the client's default
/// "discrete-first" GPU pick, so a wrong guess only changes the default - the
/// user can still select any GPU explicitly.
/// </summary>
internal static class GpuClassifier
{
    public static (string Vendor, bool Integrated) FromName(string name)
    {
        var n = name.ToLowerInvariant();

        // Apple Silicon GPU is always integrated.
        if (n.Contains("apple"))
        {
            return ("apple", true);
        }

        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")
            || n.Contains("rtx") || n.Contains("gtx") || n.Contains("tesla"))
        {
            return ("nvidia", false);
        }

        // Arc is Intel's discrete line; UHD/Iris/HD Graphics are integrated.
        if (n.Contains("intel"))
        {
            return ("intel", !n.Contains("arc"));
        }

        // AMD APUs surface as "Radeon Graphics" / "Radeon Vega N Graphics" with no
        // model number; discrete cards carry an "RX"/"Pro" marker.
        if (n.Contains("radeon") || n.Contains("amd") || n.Contains("vega"))
        {
            return ("amd", n.Contains("graphics") && !n.Contains("rx") && !n.Contains("pro"));
        }

        return ("", false);
    }
}
