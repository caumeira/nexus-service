#if LINUX
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Tests;

public class AmdGpuSysfsTests
{
    // BuildReadout is pure (no I/O), so a fixture CardReading exercises the
    // sensor mapping without touching any real /sys path.
    [Fact]
    public void BuildReadout_MapsEveryReadingIntoItsOwnSensor()
    {
        var reading = new AmdGpuSysfs.CardReading(
            BusyPercent: 42,
            VramUsedBytes: 2L * 1024 * 1024 * 1024,
            VramTotalBytes: 8L * 1024 * 1024 * 1024,
            TempC: 65.5f,
            MemTempC: 70.2f,
            PowerW: 123.4f,
            CoreClockMhz: 2500f,
            MemClockMhz: 1000f,
            FanPwm: 128);

        var readout = AmdGpuSysfs.BuildReadout(0, "AMD Radeon RX 7900 XT", "amd", false, reading);

        Assert.Equal("gpu/0", readout.Id);
        Assert.Equal("AMD Radeon RX 7900 XT", readout.Name);
        Assert.Equal("amd", readout.Vendor);
        Assert.False(readout.Integrated);

        var byId = readout.Sensors.ToDictionary(s => s.Id);
        Assert.Equal("GPU Core", byId["gpu/0/load"].Name);
        Assert.Equal(42f, byId["gpu/0/load"].Value);
        Assert.Equal(8f, byId["gpu/0/memory-total"].Value);
        Assert.Equal(2f, byId["gpu/0/memory-used"].Value);
        Assert.Equal(6f, byId["gpu/0/memory-free"].Value);
        Assert.Equal(25f, byId["gpu/0/memory-load"].Value);
        Assert.Equal(65.5f, byId["gpu/0/temp"].Value);
        Assert.Equal(70.2f, byId["gpu/0/memory-temp"].Value);
        Assert.Equal(123.4f, byId["gpu/0/power"].Value);
        Assert.Equal(2500f, byId["gpu/0/core-clock"].Value);
        Assert.Equal(1000f, byId["gpu/0/memory-clock"].Value);
        Assert.Equal(128 * 100f / 255f, byId["gpu/0/fan"].Value);
    }

    [Fact]
    public void BuildReadout_SkipsAMissingMetricsSensor_NeverTheWholeCard()
    {
        // Only busy percent readable: every other sysfs node was missing.
        var reading = new AmdGpuSysfs.CardReading(
            BusyPercent: 10,
            VramUsedBytes: null,
            VramTotalBytes: null,
            TempC: null,
            MemTempC: null,
            PowerW: null,
            CoreClockMhz: null,
            MemClockMhz: null,
            FanPwm: null);

        var readout = AmdGpuSysfs.BuildReadout(1, "AMD GPU", "amd", false, reading);

        Assert.Equal("gpu/1", readout.Id);
        var sensor = Assert.Single(readout.Sensors);
        Assert.Equal("gpu/1/load", sensor.Id);
    }

    [Fact]
    public void BuildReadout_SkipsUsedFreeLoad_WhenTotalIsReadableButUsedIsNot()
    {
        var reading = new AmdGpuSysfs.CardReading(
            BusyPercent: null,
            VramUsedBytes: null,
            VramTotalBytes: 8L * 1024 * 1024 * 1024,
            TempC: null,
            MemTempC: null,
            PowerW: null,
            CoreClockMhz: null,
            MemClockMhz: null,
            FanPwm: null);

        var readout = AmdGpuSysfs.BuildReadout(0, "AMD GPU", "amd", false, reading);

        var sensor = Assert.Single(readout.Sensors);
        Assert.Equal("gpu/0/memory-total", sensor.Id);
    }

    // EnumerateAmdCardDirs/ReadCard touch real files, so a temp tree mimics
    // /sys/class/drm/cardN/device with a driver symlink resolving to a real
    // (in-tree) directory named after the driver, matching LinuxSysfs.ReadLinkLeaf.
    private static void AddCard(TempDir t, string cardName, string driverName, bool withHwmon = true)
    {
        var driverDir = t.Dir($"drivers/{cardName}-{driverName}/{driverName}");
        t.Symlink($"{cardName}/device/driver", driverDir);
        t.Write($"{cardName}/device/gpu_busy_percent", "17\n");
        t.Write($"{cardName}/device/mem_info_vram_used", "1073741824\n");
        t.Write($"{cardName}/device/mem_info_vram_total", "8589934592\n");
        if (withHwmon)
        {
            t.Write($"{cardName}/device/hwmon/hwmon3/temp1_input", "55000\n");
            t.Write($"{cardName}/device/hwmon/hwmon3/temp3_input", "60000\n");
            t.Write($"{cardName}/device/hwmon/hwmon3/power1_average", "150000000\n");
            t.Write($"{cardName}/device/hwmon/hwmon3/freq1_input", "2100000000\n");
            t.Write($"{cardName}/device/hwmon/hwmon3/freq2_input", "1250000000\n");
            t.Write($"{cardName}/device/hwmon/hwmon3/pwm1", "180\n");
        }
    }

    [Fact]
    public void EnumerateAmdCardDirs_OnlyYieldsCardsWhoseDriverIsAmdgpu()
    {
        using var t = new TempDir();
        AddCard(t, "card0", "amdgpu");
        AddCard(t, "card1", "nouveau");
        // Per-connector dir sysfs mixes in alongside cardN - must be filtered
        // out even though its name also starts with "card" (no driver
        // symlink here either, so it would fail open() before the filter).
        t.Dir("card0-HDMI-A-1");

        var found = AmdGpuSysfs.EnumerateAmdCardDirs(t.Root).ToList();

        var only = Assert.Single(found);
        Assert.Equal(t.At("card0/device"), only);
    }

    [Fact]
    public void ReadCard_ReadsDeviceAndHwmonNodes()
    {
        using var t = new TempDir();
        AddCard(t, "card0", "amdgpu");

        var reading = AmdGpuSysfs.ReadCard(t.At("card0/device"));

        Assert.Equal(17, reading.BusyPercent);
        Assert.Equal(1073741824L, reading.VramUsedBytes);
        Assert.Equal(8589934592L, reading.VramTotalBytes);
        Assert.Equal(55f, reading.TempC);
        Assert.Equal(60f, reading.MemTempC);
        Assert.Equal(150f, reading.PowerW);
        Assert.Equal(2100f, reading.CoreClockMhz);
        Assert.Equal(1250f, reading.MemClockMhz);
        Assert.Equal(180, reading.FanPwm);
    }

    [Fact]
    public void ReadCard_LeavesHwmonFieldsNull_WhenNoHwmonDirExists()
    {
        using var t = new TempDir();
        AddCard(t, "card0", "amdgpu", withHwmon: false);

        var reading = AmdGpuSysfs.ReadCard(t.At("card0/device"));

        Assert.Equal(17, reading.BusyPercent);
        Assert.Null(reading.TempC);
        Assert.Null(reading.PowerW);
        Assert.Null(reading.CoreClockMhz);
        Assert.Null(reading.FanPwm);
    }
}
#endif
