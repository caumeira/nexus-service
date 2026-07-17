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
        bool hasOnScreenBounds = true) =>
        WindowClassification.IsCountableWindow(isVisible, owner, isToolWindow, isCloaked, hasTitle, hasOnScreenBounds);

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
    public void IsCountableWindow_False_ForATitlelessWindow()
    {
        Assert.False(RealAppWindow(hasTitle: false));
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
}
