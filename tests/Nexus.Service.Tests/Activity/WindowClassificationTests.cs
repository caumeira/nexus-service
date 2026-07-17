using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowClassificationTests
{
    [Fact]
    public void IsCountableWindow_True_ForAVisibleUnownedWindow()
    {
        Assert.True(WindowClassification.IsCountableWindow(isVisible: true, owner: IntPtr.Zero));
    }

    [Fact]
    public void IsCountableWindow_False_ForAHiddenWindow()
    {
        Assert.False(WindowClassification.IsCountableWindow(isVisible: false, owner: IntPtr.Zero));
    }

    [Fact]
    public void IsCountableWindow_False_ForAnOwnedWindow()
    {
        // A non-zero owner means this is a child/popup of another top-level
        // window (e.g. a dialog), not itself an app's main window.
        Assert.False(WindowClassification.IsCountableWindow(isVisible: true, owner: new IntPtr(1)));
    }

    [Fact]
    public void IsCountableWindow_False_ForAHiddenOwnedWindow()
    {
        Assert.False(WindowClassification.IsCountableWindow(isVisible: false, owner: new IntPtr(1)));
    }
}
