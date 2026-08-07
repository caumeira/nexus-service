using System;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// SMC die-temperature key sets and averaging shared by the cooling
/// temperature sources (MacFanControlProvider) and the monitoring sensor
/// surface (MacSensorProvider). Keys cover Apple Silicon (Tp*/Tg*/Te*)
/// with Intel-era fallbacks (TC*/TG*); absent keys read as null.
/// </summary>
internal static class MacSmcTemperatures
{
    public static readonly string[] CpuKeys =
    {
        "Tp01", "Tp05", "Tp09", "Tp0D", "Tp02", "Tp06", "Tp0A", "Tp0E",
        "Te05", "Te09", "TC0P", "TC0E", "TC0F",
    };

    public static readonly string[] GpuKeys =
    {
        "Tg05", "Tg0D", "Tg0H", "Tg0L", "Tg0P", "Tg0T", "TG0P", "TG0D",
    };

    public static readonly string[] SsdKeys =
    {
        "TH0a", "TH0b", "TH0c", "TH0x",
    };

    /// <summary>
    /// Mean of the keys that read a physically plausible temperature; a key
    /// that reads as absent or outside the sensor's physical range must not
    /// skew the average. Null when no key qualifies.
    /// </summary>
    public static float? Average(Func<string, float?> readKey, string[] keys)
    {
        float sum = 0f;
        int n = 0;
        foreach (var k in keys)
        {
            var v = readKey(k);
            if (v.HasValue && v.Value > 0f && v.Value < 150f)
            {
                sum += v.Value;
                n++;
            }
        }
        return n == 0 ? null : sum / n;
    }
}
