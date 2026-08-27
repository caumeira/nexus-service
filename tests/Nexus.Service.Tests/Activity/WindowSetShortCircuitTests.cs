using System;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// WindowSetPoller skips the two cross-process calls (DwmGetWindowAttribute and
/// GetWindowTextLength) for any window failing the cheap gate, leaving their
/// four derived inputs at false. That is only sound if those inputs cannot
/// change the classification when the gate is false. Proven here exhaustively
/// rather than argued: every cheap-input combination crossed with every
/// expensive-input combination.
/// </summary>
public class WindowSetShortCircuitTests
{
    // Mirrors the condition in WindowSetPoller.SnapshotWindowedPids.
    private static bool NeedsExpensiveInputs(
        bool ownerNonZero, bool hasOnScreenBounds, bool isForegroundWindow, bool isVisible, bool isToolWindow)
        => !ownerNonZero && hasOnScreenBounds && !isForegroundWindow && isVisible && !isToolWindow;

    [Fact]
    public void SkippedInputs_CannotChangeClassification_ForAnyGatedWindow()
    {
        var checkedCombos = 0;

        foreach (var ownerNonZero in Bools)
        foreach (var hasOnScreenBounds in Bools)
        foreach (var isForegroundWindow in Bools)
        foreach (var isVisible in Bools)
        foreach (var isToolWindow in Bools)
        {
            if (NeedsExpensiveInputs(ownerNonZero, hasOnScreenBounds, isForegroundWindow, isVisible, isToolWindow))
            {
                continue; // the poller does compute them here, so nothing to prove
            }

            var owner = ownerNonZero ? new IntPtr(1) : IntPtr.Zero;

            // What the poller now passes: the four skipped inputs left at false.
            var skipped = WindowClassification.IsCountableWindow(
                isVisible, owner, isToolWindow,
                isCloaked: false, hasTitle: false, hasOnScreenBounds,
                coversMonitor: false, titleBlockedByUipi: false, isForegroundWindow);

            foreach (var isCloaked in Bools)
            foreach (var coversMonitor in Bools)
            foreach (var hasTitle in Bools)
            foreach (var titleBlockedByUipi in Bools)
            {
                var actual = WindowClassification.IsCountableWindow(
                    isVisible, owner, isToolWindow,
                    isCloaked, hasTitle, hasOnScreenBounds,
                    coversMonitor, titleBlockedByUipi, isForegroundWindow);

                Assert.True(skipped == actual,
                    $"gated window reclassified: owner={ownerNonZero} bounds={hasOnScreenBounds} " +
                    $"fg={isForegroundWindow} visible={isVisible} tool={isToolWindow} | " +
                    $"cloaked={isCloaked} covers={coversMonitor} title={hasTitle} uipi={titleBlockedByUipi}");
                checkedCombos++;
            }
        }

        // Guards the guard: a gate that accidentally admitted everything would
        // make the loop above vacuous and still pass.
        Assert.True(checkedCombos > 0, "no gated combinations were exercised");
    }

    // The gate must not skip the calls for a window whose classification does
    // depend on them - that would be a real behaviour change, not a saving.
    [Fact]
    public void UngatedWindows_AreExactlyThoseWhoseOutcomeDependsOnTheSkippedInputs()
    {
        foreach (var ownerNonZero in Bools)
        foreach (var hasOnScreenBounds in Bools)
        foreach (var isForegroundWindow in Bools)
        foreach (var isVisible in Bools)
        foreach (var isToolWindow in Bools)
        {
            var owner = ownerNonZero ? new IntPtr(1) : IntPtr.Zero;
            var dependsOnSkipped = false;
            var baseline = WindowClassification.IsCountableWindow(
                isVisible, owner, isToolWindow, false, false, hasOnScreenBounds, false, false, isForegroundWindow);

            foreach (var isCloaked in Bools)
            foreach (var coversMonitor in Bools)
            foreach (var hasTitle in Bools)
            foreach (var titleBlockedByUipi in Bools)
            {
                if (WindowClassification.IsCountableWindow(
                        isVisible, owner, isToolWindow, isCloaked, hasTitle,
                        hasOnScreenBounds, coversMonitor, titleBlockedByUipi, isForegroundWindow) != baseline)
                {
                    dependsOnSkipped = true;
                }
            }

            if (dependsOnSkipped)
            {
                Assert.True(
                    NeedsExpensiveInputs(ownerNonZero, hasOnScreenBounds, isForegroundWindow, isVisible, isToolWindow),
                    "a window whose outcome depends on the expensive inputs was gated out of computing them");
            }
        }
    }

    private static readonly bool[] Bools = { false, true };
}
