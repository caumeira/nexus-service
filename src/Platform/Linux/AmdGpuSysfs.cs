#if LINUX
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Reads amdgpu sysfs telemetry under /sys/class/drm/cardN/device. AMD has no
/// vendor CLI tool the way NVIDIA has nvidia-smi, so every metric is its own
/// sysfs node; each read is individually best-effort and a missing node
/// yields no sensor for that metric, never a dropped card.
/// </summary>
internal static class AmdGpuSysfs
{
    private const string DrmRoot = "/sys/class/drm";

    /// <summary>One card's raw readings before they become HardwareSensors -
    /// every field independently optional so BuildReadout can skip exactly
    /// the metrics that were unreadable.</summary>
    internal readonly record struct CardReading(
        int? BusyPercent,
        long? VramUsedBytes,
        long? VramTotalBytes,
        float? TempC,
        float? MemTempC,
        float? PowerW,
        float? CoreClockMhz,
        float? MemClockMhz,
        int? FanPwm);

    /// <summary>Card device dirs (/sys/class/drm/cardN/device) whose driver
    /// symlink resolves to amdgpu, in card-name order.</summary>
    internal static IEnumerable<string> EnumerateAmdCardDirs() => EnumerateAmdCardDirs(DrmRoot);

    internal static IEnumerable<string> EnumerateAmdCardDirs(string drmRoot)
    {
        List<string> dirs;
        try
        {
            // .NET search patterns only support * and ? - [0-9] is matched
            // literally, not as a character class - so filter card* by hand
            // below to also exclude the per-connector cardN-<output> dirs
            // (e.g. card0-HDMI-A-1) that /sys/class/drm mixes in alongside cardN.
            dirs = new List<string>(Directory.EnumerateDirectories(drmRoot, "card*"));
        }
        catch
        {
            yield break;
        }
        dirs.Sort(StringComparer.Ordinal);

        foreach (var card in dirs)
        {
            var name = Path.GetFileName(card);
            if (!IsCardDeviceDirName(name))
            {
                continue;
            }
            var device = Path.Combine(card, "device");
            var driverLeaf = LinuxSysfs.ReadLinkLeaf(Path.Combine(device, "driver"));
            if (string.Equals(driverLeaf, "amdgpu", StringComparison.Ordinal))
            {
                yield return device;
            }
        }
    }

    // "cardN" only - excludes the "cardN-<connector>" per-output dirs
    // (card0-HDMI-A-1, card0-eDP-1, ...) sysfs lists alongside the card itself.
    private static bool IsCardDeviceDirName(string name)
    {
        if (!name.StartsWith("card", StringComparison.Ordinal) || name.Length <= 4)
        {
            return false;
        }
        for (var i = 4; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reads every metric for one card's device dir. Never throws.</summary>
    internal static CardReading ReadCard(string deviceDir)
    {
        var busyPercent = ReadIntOrNull(Path.Combine(deviceDir, "gpu_busy_percent"));
        var vramUsed = ReadLongOrNull(Path.Combine(deviceDir, "mem_info_vram_used"));
        var vramTotal = ReadLongOrNull(Path.Combine(deviceDir, "mem_info_vram_total"));

        float? tempC = null;
        float? memTempC = null;
        float? powerW = null;
        float? coreClockMhz = null;
        float? memClockMhz = null;
        int? fanPwm = null;

        var hwmonDir = FindHwmonDir(deviceDir);
        if (hwmonDir is not null)
        {
            tempC = ReadMilliOrNull(Path.Combine(hwmonDir, "temp1_input"));
            memTempC = ReadMilliOrNull(Path.Combine(hwmonDir, "temp3_input"));

            var microWatts = ReadLongOrNull(Path.Combine(hwmonDir, "power1_average"))
                ?? ReadLongOrNull(Path.Combine(hwmonDir, "power1_input"));
            powerW = microWatts is long uw ? uw / 1_000_000f : null;

            var coreHz = ReadLongOrNull(Path.Combine(hwmonDir, "freq1_input"));
            coreClockMhz = coreHz is long chz ? chz / 1_000_000f : null;

            var memHz = ReadLongOrNull(Path.Combine(hwmonDir, "freq2_input"));
            memClockMhz = memHz is long mhz ? mhz / 1_000_000f : null;

            fanPwm = ReadIntOrNull(Path.Combine(hwmonDir, "pwm1"));
        }

        return new CardReading(busyPercent, vramUsed, vramTotal, tempC, memTempC, powerW, coreClockMhz, memClockMhz, fanPwm);
    }

    /// <summary>Builds sensors from an already-read card sample; pure, no I/O.</summary>
    internal static GpuReadout BuildReadout(int gpuIndex, string name, string vendor, bool integrated, CardReading reading)
    {
        var sensors = new List<HardwareSensor>();

        if (reading.BusyPercent is int busy)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/load", "GPU Core", "Load", busy, "%", name));
        }

        if (reading.VramTotalBytes is long vramTotal && vramTotal > 0)
        {
            var totalGb = vramTotal / 1024f / 1024f / 1024f;
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-total", "GPU Memory", "SmallData", totalGb, "GB", name));

            if (reading.VramUsedBytes is long vramUsed)
            {
                var usedGb = vramUsed / 1024f / 1024f / 1024f;
                var freeGb = totalGb - usedGb;
                sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-used", "GPU Memory Used", "SmallData", usedGb, "GB", name, theoreticalMax: totalGb));
                sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-free", "GPU Memory Free", "SmallData", freeGb, "GB", name, theoreticalMax: totalGb));
                sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-load", "GPU Memory Load", "Load", vramUsed * 100f / vramTotal, "%", name));
            }
        }

        if (reading.TempC is float tempC)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/temp", "GPU Temperature", "Temperature", tempC, "°C", name));
        }
        if (reading.MemTempC is float memTempC)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-temp", "GPU Memory Temp", "Temperature", memTempC, "°C", name));
        }
        if (reading.PowerW is float powerW)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/power", "GPU Power", "Power", powerW, "W", name));
        }
        if (reading.CoreClockMhz is float coreClk)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/core-clock", "GPU Core Clock", "Clock", coreClk, "MHz", name));
        }
        if (reading.MemClockMhz is float memClk)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/memory-clock", "GPU Memory Clock", "Clock", memClk, "MHz", name));
        }
        if (reading.FanPwm is int pwm)
        {
            sensors.Add(LinuxSensorProvider.MakeSensor($"gpu/{gpuIndex}/fan", "GPU Fan", "Load", pwm * 100f / 255f, "%", name));
        }

        return new GpuReadout
        {
            Id = $"gpu/{gpuIndex}",
            Name = name,
            Vendor = vendor,
            Integrated = integrated,
            Sensors = sensors,
        };
    }

    private static string? FindHwmonDir(string deviceDir)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(deviceDir, "hwmon"), "hwmon*"))
            {
                return dir;
            }
        }
        catch { }
        return null;
    }

    private static int? ReadIntOrNull(string path)
        => int.TryParse(LinuxSysfs.ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? ReadLongOrNull(string path)
        => long.TryParse(LinuxSysfs.ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    // amdgpu hwmon temps are millidegrees C; non-positive means unpopulated.
    private static float? ReadMilliOrNull(string path)
        => long.TryParse(LinuxSysfs.ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli) && milli > 0
            ? milli / 1000f
            : null;
}
#endif
