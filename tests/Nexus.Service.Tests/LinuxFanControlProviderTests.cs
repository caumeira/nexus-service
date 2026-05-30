using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Drives LinuxFanControlProvider against a fake hwmon tree: enumeration,
/// duty↔pwm scaling, mode, the set/release sysfs writes, temps, and the
/// calibration ramp — all without real hardware (1ms settle).
/// </summary>
public class LinuxFanControlProviderTests
{
    private static LinuxFanControlProvider WithControllableFan(TempDir t, int pwm, int enable, int rpm)
    {
        t.Write("hwmon/hwmon0/name", "nct6779\n");
        t.Write("hwmon/hwmon0/pwm2", pwm + "\n");
        t.Write("hwmon/hwmon0/pwm2_enable", enable + "\n");
        t.Write("hwmon/hwmon0/fan2_input", rpm + "\n");
        t.Write("hwmon/hwmon0/fan2_label", "CPU Fan\n");
        return new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void GetFanChannels_ReportsDutyRpmMode()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 128, enable: 1, rpm: 900);
        var ch = Assert.Single(p.GetFanChannels());
        Assert.Equal("linux/fan/nct6779/2", ch.Id);
        Assert.Equal("nct6779 CPU Fan", ch.Name);
        Assert.Equal(50, ch.DutyPercent); // 128/255 ≈ 50%
        Assert.Equal(900, ch.Rpm);
        Assert.Equal(FanModes.Manual, ch.Mode); // enable=1
    }

    [Fact]
    public void GetFanChannels_SkipsFanWithoutEnableKnob()
    {
        using var t = new TempDir();
        t.Write("hwmon/hwmon0/name", "it87\n");
        t.Write("hwmon/hwmon0/pwm1", "200\n"); // no pwm1_enable -> not controllable
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
        Assert.Empty(p.GetFanChannels());
    }

    [Fact]
    public void SetFanSpeed_WritesPwmAndManualEnable()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 0, enable: 2, rpm: 0);
        Assert.Equal(75, p.SetFanSpeed("linux/fan/nct6779/2", 75));
        Assert.Equal("1", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable")));
        Assert.Equal("191", File.ReadAllText(t.At("hwmon/hwmon0/pwm2"))); // 75% of 255
    }

    [Fact]
    public void ReleaseFan_RestoresAutomatic()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 191, enable: 1, rpm: 0);
        p.GetFanChannels();
        p.ReleaseFan("linux/fan/nct6779/2");
        Assert.Equal("2", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable")));
    }

    [Fact]
    public void GetTemperatureSources_ReadsHwmonTempsByCategory()
    {
        using var t = new TempDir();
        t.Write("hwmon/hwmon0/name", "k10temp\n");
        t.Write("hwmon/hwmon0/temp1_input", "45000\n");
        t.Write("hwmon/hwmon0/temp1_label", "Tctl\n");
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
        var src = Assert.Single(p.GetTemperatureSources());
        Assert.Equal("CPU", src.Category);
        Assert.Equal("k10temp Tctl", src.Name);
        Assert.Equal(45f, src.Value);
        Assert.Equal(45f, p.ReadTemperature(src.Id));
    }

    [Fact]
    public async Task CalibrateAsync_RampsAllStepsAndRestores()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 255, enable: 2, rpm: 1200);
        var cal = Assert.Single(await p.CalibrateAsync(Array.Empty<string>(), null!, CancellationToken.None));
        Assert.Equal(11, cal.Curve.Count); // 100..0 in 11 steps
        Assert.Equal(1200, cal.MaxRpm);
        Assert.Equal("2", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable"))); // restored
    }
}
