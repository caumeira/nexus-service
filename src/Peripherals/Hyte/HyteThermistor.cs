using System;

namespace Nexus.Service.Peripherals.Hyte;

/// <summary>
/// NTC thermistor curves shared by every HYTE cooling device (NP50 hub and per-fan probes,
/// Q-series coolant probes and firmware curve). Ported verbatim from nexus-control-service
/// LightDancing/Common/SmartDeviceCommon/SmartDeviceMethods.cs; pump and fan probes sit on
/// distinct dividers and must not be read against each other's table.
/// </summary>
internal static class HyteThermistor
{
    /// <summary>Highest °C the tables cover; index 0..MaxTempC.</summary>
    public const int MaxTempC = 75;

    /// <summary>
    /// Live ADC bytes to volts. The firmware packs the 0-4095 count decimal-style as
    /// high = hundreds, low = remainder (0-99), so this is NOT a big-endian uint16.
    /// </summary>
    public static double VoltageFromAdc(byte high, byte low) => 3.3 * (high * 100.0 + low) / 4096.0;

    /// <summary>Table voltage for a °C, clamped into range. Used when encoding a firmware curve point.</summary>
    public static double VoltageAt(int tempC, bool fan) => Table(fan)[Math.Clamp(tempC, 0, MaxTempC)];

    /// <summary>Nearest table index to a voltage, without saturation rejection.</summary>
    public static int NearestIndex(double voltage, bool fan)
    {
        var table = Table(fan);
        var best = 0;
        var bestDelta = double.MaxValue;
        for (var i = 0; i < table.Length; i++)
        {
            var delta = Math.Abs(voltage - table[i]);
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Nearest °C to a live probe voltage, or null when it saturates either end of the table.
    /// Saturation means the input is outside the thermistor's calibrated span, which is how a
    /// module with no probe wired to it reads; the reference discards the hot end the same way
    /// (CoolingHubBaseController: <c>temp == 75 ? previous : temp</c>).
    /// </summary>
    public static float? NearestTempC(double voltage, bool fan)
    {
        var i = NearestIndex(voltage, fan);
        if (i <= 0 || i >= MaxTempC) return null;
        return i;
    }

    private static double[] Table(bool fan) => fan ? FanVoltage : PumpVoltage;

    // temp(°C) = index -> sensor voltage. SmartDeviceMethods._pumpTempToVoltageMapping.
    private static readonly double[] PumpVoltage =
    {
        3.04, 3.033, 3.025, 3.012, 3.009, 3.0, 2.995, 2.987, 2.98, 2.976,
        2.968, 2.954, 2.949, 2.943, 2.932, 2.918, 2.89, 2.882, 2.874, 2.866,
        2.84, 2.832, 2.826, 2.789, 2.773, 2.755, 2.74, 2.715, 2.694, 2.682,
        2.669, 2.644, 2.631, 2.611, 2.591, 2.57, 2.555, 2.53, 2.515, 2.496,
        2.469, 2.452, 2.433, 2.408, 2.387, 2.361, 2.346, 2.318, 2.299, 2.277,
        2.261, 2.233, 2.207, 2.18, 2.155, 2.133, 2.103, 2.084, 2.058, 2.034,
        2.011, 1.988, 1.964, 1.93, 1.911, 1.884, 1.858, 1.836, 1.807, 1.781,
        1.761, 1.734, 1.704, 1.681, 1.654, 1.643,
    };

    // SmartDeviceMethods._fanTempToVoltageMapping.
    private static readonly double[] FanVoltage =
    {
        3.22, 3.21, 3.2, 3.19, 3.181, 3.17, 3.159, 3.146, 3.133, 3.125,
        3.104, 3.099, 3.088, 3.075, 3.063, 3.05, 3.034, 3.023, 3.012, 2.999,
        2.984, 2.968, 2.952, 2.94, 2.921, 2.904, 2.886, 2.872, 2.852, 2.837,
        2.816, 2.798, 2.783, 2.761, 2.745, 2.729, 2.7, 2.684, 2.666, 2.635,
        2.624, 2.603, 2.58, 2.558, 2.538, 2.515, 2.492, 2.472, 2.449, 2.427,
        2.404, 2.383, 2.363, 2.338, 2.311, 2.29, 2.264, 2.241, 2.217, 2.194,
        2.168, 2.143, 2.115, 2.093, 2.066, 2.042, 2.02, 1.998, 1.976, 1.947,
        1.927, 1.902, 1.876, 1.853, 1.828, 1.803,
    };
}
