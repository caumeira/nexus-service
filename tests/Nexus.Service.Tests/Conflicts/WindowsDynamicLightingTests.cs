using System;
using Nexus.Service.Conflicts;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The registry halves are Windows-only and are verified on a lab box. The
/// absent-value rule is the one piece of logic that decides what the user sees
/// on a machine Windows has never written these values on.
/// </summary>
public class WindowsDynamicLightingTests
{
    [Fact]
    public void AnAbsentValueReadsAsOn()
    {
        // Windows writes these only once the page is touched, and both default
        // to on - reading absent as off would show the section already handled.
        Assert.True(WindowsDynamicLighting.DwordIsOn(null));
    }

    [Fact]
    public void ZeroIsTheOnlyOffValue()
    {
        Assert.False(WindowsDynamicLighting.DwordIsOn(0));
        Assert.True(WindowsDynamicLighting.DwordIsOn(1));
        Assert.True(WindowsDynamicLighting.DwordIsOn(2));
    }

    [Fact]
    public void AValueOfTheWrongTypeReadsAsOn()
    {
        // A REG_SZ where a DWORD was expected means the recipe stopped
        // matching Windows; reporting on keeps the action offered.
        Assert.True(WindowsDynamicLighting.DwordIsOn("0"));
    }

    [Fact]
    public void OnlyLampArrayInterfacesCount()
    {
        // Every other HID interface on the box shares the class GUID, so the
        // usage is the whole of what separates a lighting device from a mouse.
        var ids = WindowsDynamicLighting.LampArrayIds(new[]
        {
            new HidDeviceInfo { Path = @"\\?\hid#vid_048d&pid_5711&mi_00#a&ced409&0&0000#{4d1e55b2}", UsagePage = 0x59, Usage = 1 },
            new HidDeviceInfo { Path = @"\\?\hid#vid_046d&pid_c52b#6&1a2b3c&0&0000#{4d1e55b2}", UsagePage = 0x01, Usage = 2 },
            new HidDeviceInfo { Path = @"\\?\hid#vid_048d&pid_5711&mi_01#a&230869b9&0&0000#{4d1e55b2}", UsagePage = 0x59, Usage = 2 },
        });

        Assert.Equal(new[] { "hid#vid_048d&pid_5711&mi_00#a&ced409&0&0000#{4d1e55b2}" }, ids);
    }

    [Fact]
    public void AnInterfacePathBecomesTheDevicesSubkeyName()
    {
        // Windows names the entry after the interface path minus its prefix;
        // keeping the prefix would create a second entry it never reads.
        var ids = WindowsDynamicLighting.LampArrayIds(new[]
        {
            new HidDeviceInfo { Path = @"\\?\hid#vid_048d&pid_5711&mi_00#a&ced409&0&0000#{4d1e55b2}", UsagePage = 0x59, Usage = 1 },
        });

        Assert.Equal("hid#vid_048d&pid_5711&mi_00#a&ced409&0&0000#{4d1e55b2}", Assert.Single(ids));
    }

    [Fact]
    public void OffWindowsNothingIsAvailable()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(WindowsDynamicLighting.IsSupported());
        Assert.False(WindowsDynamicLighting.Read().Available);
        Assert.False(WindowsDynamicLighting.Write(enabled: false).Available);
    }
}
