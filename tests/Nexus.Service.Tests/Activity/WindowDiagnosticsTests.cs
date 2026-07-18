using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowDiagnosticsTests
{
    [Fact]
    public void IsEnabled_False_ForNull()
    {
        Assert.False(WindowDiagnostics.IsEnabled(null));
    }

    [Fact]
    public void IsEnabled_False_ForEmptyString()
    {
        Assert.False(WindowDiagnostics.IsEnabled(""));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("TRUE")]
    [InlineData(" 1")]
    public void IsEnabled_False_ForAnyValueOtherThanTheLiteralOne(string value)
    {
        Assert.False(WindowDiagnostics.IsEnabled(value));
    }

    [Fact]
    public void IsEnabled_True_ForTheLiteralOne()
    {
        Assert.True(WindowDiagnostics.IsEnabled("1"));
    }

    [Fact]
    public void HelperArgName_IsDistinctFromTheEnvVarName()
    {
        // UserHelperBootstrapper forwards this literal as an argv token and
        // WindowsUserHelper.Run matches on the same constant to re-hydrate
        // EnvVarName inside the helper process - the two must never collide.
        Assert.NotEqual(WindowDiagnostics.EnvVarName, WindowDiagnostics.HelperArgName);
        Assert.StartsWith("--", WindowDiagnostics.HelperArgName);
    }

    [Fact]
    public void FormatLine_IncludesAllClassifierInputsAndTheResult()
    {
        var line = WindowDiagnostics.FormatLine(
            pid: 4242,
            processName: "SystemShock",
            isVisible: true,
            hasOwner: false,
            isToolWindow: false,
            isCloaked: false,
            hasTitle: false,
            titleLength: 0,
            titleReadError: 5,
            hasOnScreenBounds: true,
            coversMonitor: false,
            isCountable: false);

        Assert.Contains("[window-diag]", line);
        Assert.Contains("pid=4242", line);
        Assert.Contains("proc=\"SystemShock\"", line);
        Assert.Contains("isVisible=True", line);
        Assert.Contains("hasOwner=False", line);
        Assert.Contains("isToolWindow=False", line);
        Assert.Contains("isCloaked=False", line);
        Assert.Contains("hasTitle=False", line);
        Assert.Contains("titleLength=0", line);
        Assert.Contains("titleReadError=5", line);
        Assert.Contains("hasOnScreenBounds=True", line);
        Assert.Contains("coversMonitor=False", line);
        Assert.Contains("isCountable=False", line);
        Assert.Contains("class=Background", line);
    }

    [Fact]
    public void FormatLine_ReportsAppForACountableWindow()
    {
        var line = WindowDiagnostics.FormatLine(
            pid: 100,
            processName: "notepad",
            isVisible: true,
            hasOwner: false,
            isToolWindow: false,
            isCloaked: false,
            hasTitle: true,
            titleLength: 8,
            titleReadError: 0,
            hasOnScreenBounds: true,
            coversMonitor: false,
            isCountable: true);

        Assert.Contains("isCountable=True", line);
        Assert.Contains("class=App", line);
    }

    [Fact]
    public void FormatLine_ReportsAppForAFullscreenGame_CloakedButCoveringAMonitor()
    {
        var line = WindowDiagnostics.FormatLine(
            pid: 200,
            processName: "SystemShock",
            isVisible: true,
            hasOwner: false,
            isToolWindow: false,
            isCloaked: true,
            hasTitle: false,
            titleLength: 0,
            titleReadError: 5,
            hasOnScreenBounds: true,
            coversMonitor: true,
            isCountable: true);

        Assert.Contains("isCloaked=True", line);
        Assert.Contains("coversMonitor=True", line);
        Assert.Contains("class=App", line);
    }

    [Fact]
    public void FormatLine_DistinguishesAnEmptyTitleFromABlockedRead()
    {
        // titleLength=0 with a nonzero titleReadError signals a blocked
        // GetWindowTextLength call (e.g. UIPI across an elevation boundary),
        // not a window that legitimately has no title.
        var blocked = WindowDiagnostics.FormatLine(
            pid: 1, processName: "elevated", isVisible: true, hasOwner: false,
            isToolWindow: false, isCloaked: false, hasTitle: false, titleLength: 0,
            titleReadError: 5, hasOnScreenBounds: true, coversMonitor: false, isCountable: false);
        var legitimatelyEmpty = WindowDiagnostics.FormatLine(
            pid: 2, processName: "other", isVisible: true, hasOwner: false,
            isToolWindow: false, isCloaked: false, hasTitle: false, titleLength: 0,
            titleReadError: 0, hasOnScreenBounds: true, coversMonitor: false, isCountable: false);

        Assert.Contains("titleReadError=5", blocked);
        Assert.Contains("titleReadError=0", legitimatelyEmpty);
    }
}
