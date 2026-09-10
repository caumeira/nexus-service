using System;
using Nexus.Service.Conflicts;
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
    public void OffWindowsNothingIsAvailable()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(WindowsDynamicLighting.IsSupported());
        Assert.False(WindowsDynamicLighting.Read().Available);
        Assert.False(WindowsDynamicLighting.Write(enabled: false, foregroundAppControl: false, deviceLighting: false).Available);
    }
}
