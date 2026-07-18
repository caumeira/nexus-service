using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowClassificationTests
{
    private static bool RealAppWindow(
        bool isVisible = true,
        IntPtr owner = default,
        bool isToolWindow = false,
        bool isCloaked = false,
        bool hasTitle = true,
        bool hasOnScreenBounds = true,
        bool coversMonitor = false,
        bool titleBlockedByUipi = false) =>
        WindowClassification.IsCountableWindow(
            isVisible, owner, isToolWindow, isCloaked, hasTitle, hasOnScreenBounds,
            coversMonitor, titleBlockedByUipi);

    [Fact]
    public void IsCountableWindow_True_ForAVisibleUnownedTitledOnScreenWindow()
    {
        Assert.True(RealAppWindow());
    }

    [Fact]
    public void IsCountableWindow_False_ForAHiddenWindow()
    {
        Assert.False(RealAppWindow(isVisible: false));
    }

    [Fact]
    public void IsCountableWindow_False_ForAnOwnedWindow()
    {
        // A non-zero owner means this is a child/popup of another top-level
        // window (e.g. a dialog), not itself an app's main window.
        Assert.False(RealAppWindow(owner: new IntPtr(1)));
    }

    [Fact]
    public void IsCountableWindow_False_ForAToolWindow()
    {
        Assert.False(RealAppWindow(isToolWindow: true));
    }

    [Fact]
    public void IsCountableWindow_False_ForACloakedWindow()
    {
        // DWM-cloaked: Explorer keeps the window alive but never shows it
        // (background UWP frames, service-owned helper windows) - the
        // single biggest source of over-counting IsWindowVisible alone missed.
        Assert.False(RealAppWindow(isCloaked: true));
    }

    [Fact]
    public void IsCountableWindow_True_ForACloakedWindow_ThatCoversAFullMonitor()
    {
        // Exclusive-fullscreen games get DWM-cloaked too, but unlike a
        // hidden background window they cover the whole monitor.
        Assert.True(RealAppWindow(isCloaked: true, coversMonitor: true));
    }

    [Fact]
    public void IsCountableWindow_False_ForACloakedWindow_ThatDoesNotCoverAMonitor()
    {
        // The svchost/background-UWP case the cloak check exists for stays
        // excluded even with the monitor-covering override in place.
        Assert.False(RealAppWindow(isCloaked: true, coversMonitor: false));
    }

    [Fact]
    public void IsCountableWindow_False_ForATitlelessWindow()
    {
        Assert.False(RealAppWindow(hasTitle: false));
    }

    [Fact]
    public void IsCountableWindow_True_ForATitlelessWindow_WhenTheReadWasBlockedByUipi()
    {
        // An elevated process's title is unreadable to the medium-integrity
        // helper (ERROR_ACCESS_DENIED) - still counted, unlike a window that
        // truly has no title.
        Assert.True(RealAppWindow(hasTitle: false, titleBlockedByUipi: true));
    }

    [Fact]
    public void IsCountableWindow_False_ForAnOwnedWindow_EvenWhenTheTitleReadWasBlocked()
    {
        // The UIPI-blocked-title override does not bypass the other gates -
        // an owned popup stays excluded regardless.
        Assert.False(RealAppWindow(owner: new IntPtr(1), titleBlockedByUipi: true));
    }

    [Fact]
    public void IsCountableWindow_False_ForAWindowWithoutOnScreenBounds()
    {
        Assert.False(RealAppWindow(hasOnScreenBounds: false));
    }

    [Fact]
    public void HasOnScreenBounds_True_ForAWindowInsideTheVirtualScreen()
    {
        Assert.True(WindowClassification.HasOnScreenBounds(
            left: 100, top: 100, right: 500, bottom: 400,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void HasOnScreenBounds_True_ForAWindowStraddlingTheVirtualScreenEdge()
    {
        Assert.True(WindowClassification.HasOnScreenBounds(
            left: -50, top: -50, right: 100, bottom: 100,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void HasOnScreenBounds_False_ForAWindowEntirelyOffScreen()
    {
        Assert.False(WindowClassification.HasOnScreenBounds(
            left: -5000, top: -5000, right: -4900, bottom: -4900,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void HasOnScreenBounds_False_ForAZeroWidthWindow()
    {
        Assert.False(WindowClassification.HasOnScreenBounds(
            left: 100, top: 100, right: 100, bottom: 400,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void HasOnScreenBounds_False_ForAZeroHeightWindow()
    {
        Assert.False(WindowClassification.HasOnScreenBounds(
            left: 100, top: 100, right: 500, bottom: 100,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void HasOnScreenBounds_False_ForANegativeSizeWindow()
    {
        // Right/bottom before left/top - malformed, not just empty.
        Assert.False(WindowClassification.HasOnScreenBounds(
            left: 500, top: 500, right: 100, bottom: 100,
            virtualLeft: 0, virtualTop: 0, virtualRight: 1920, virtualBottom: 1080));
    }

    [Fact]
    public void CoversMonitor_True_ForAWindowExactlyMatchingTheMonitorBounds()
    {
        Assert.True(WindowClassification.CoversMonitor(
            left: 0, top: 0, right: 1920, bottom: 1080,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080));
    }

    [Fact]
    public void CoversMonitor_True_ForAWindowLargerThanTheMonitor()
    {
        // Overscan/border padding some exclusive-fullscreen presenters add.
        Assert.True(WindowClassification.CoversMonitor(
            left: -10, top: -10, right: 1930, bottom: 1090,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080));
    }

    [Fact]
    public void CoversMonitor_False_ForAWindowSmallerThanTheMonitor()
    {
        Assert.False(WindowClassification.CoversMonitor(
            left: 100, top: 100, right: 500, bottom: 400,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080));
    }

    [Fact]
    public void CoversMonitor_False_ForAZeroSizeWindow()
    {
        Assert.False(WindowClassification.CoversMonitor(
            left: 100, top: 100, right: 100, bottom: 400,
            monitorLeft: 0, monitorTop: 0, monitorRight: 1920, monitorBottom: 1080));
    }
}
