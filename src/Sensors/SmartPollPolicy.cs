namespace Nexus.Service.Sensors;

/// <summary>
/// Seconds-between-SMART-reads, per drive. A SMART read is an ATA pass-through that
/// reloads a parked head on a rotational drive, so the interval is the user's to set.
/// </summary>
public static class SmartPollPolicy
{
    /// <summary>0 = never read SMART for that drive.</summary>
    public const int NeverSeconds = 0;
    public const int MinSeconds = 1;
    public const int MaxSeconds = 300;

    /// <summary>Offered in the UI as a discrete set; a value off this list still clamps into range.</summary>
    public static readonly int[] Choices = { 1, 2, 3, 4, 5, 10, 20, 30, 60, 120, 180, 240, 300, NeverSeconds };

    /// <summary>Only a rotational drive parks heads, so an SSD/NVMe has no reason to be slowed.</summary>
    public const int RotationalDefaultSeconds = 30;
    public const int SolidStateDefaultSeconds = 1;

    /// <summary>Unknown media is treated as rotational: the cost of being wrong that way is latency, not wear.</summary>
    public static int DefaultSecondsFor(bool? rotational) =>
        rotational == false ? SolidStateDefaultSeconds : RotationalDefaultSeconds;

    public static int ClampSeconds(int seconds)
    {
        if (seconds <= NeverSeconds) return NeverSeconds;
        return seconds < MinSeconds ? MinSeconds : seconds > MaxSeconds ? MaxSeconds : seconds;
    }

    public static Dictionary<string, int> Sanitize(Dictionary<string, int> raw)
    {
        var result = new Dictionary<string, int>(raw.Count, StringComparer.Ordinal);
        foreach (var (id, seconds) in raw)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            result[id] = ClampSeconds(seconds);
        }
        return result;
    }

    /// <summary>
    /// Update cycles between SMART reads, for LibreHardwareMonitor's StorageDevice.SmartUpdateCycleCount.
    /// The hardware walk is capped at 1Hz, so a cycle is a second; uint.MaxValue is never.
    /// </summary>
    public static uint ToCycleCount(int seconds) =>
        seconds <= NeverSeconds ? uint.MaxValue : (uint)ClampSeconds(seconds);
}
