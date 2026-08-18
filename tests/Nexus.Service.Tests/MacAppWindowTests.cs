using Nexus.Service.Platform.Mac;
using Xunit;

namespace Nexus.Service.Tests;

// Pure geometry of the titlebar drag strip's click-through columns, in
// lockstep with the layout constants mirrored from TopBar.module.scss. The
// left cluster holds the sidebar-collapse toggle AND the Focus toggle; a
// carve-out spanning only one button leaves the second click-dead under the
// drag strip (its clicks start a window drag instead).
public class MacAppWindowTests
{
    // Wide enough that the mode toggle clamps at its max (w - 1000 >= 220).
    private const double BarWidth = 1260;
    // .topBar padding-left + --mac-traffic-light-inset.
    private const double ClusterLeft = 8 + 96;
    // Search pill right edge (bar center + half --search-w) + pill->gear gap (0.4rem).
    private const double PageSettingsLeft = BarWidth / 2 + 460 / 2.0 + 7;
    // .modeToggleButton max-width + .pageSettings cluster gap (0.3rem) + .pageSettingsButton width.
    private const double PageSettingsClusterWidth = 220 + 4.8 + 34;

    [Theory]
    [InlineData(ClusterLeft + 16)]              // collapse toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 16)]   // Focus toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 30)]   // Focus toggle right edge
    [InlineData(BarWidth / 2)]                  // search pill center
    [InlineData(PageSettingsLeft + 100)]        // mid mode-toggle text button
    [InlineData(PageSettingsLeft + PageSettingsClusterWidth)] // gear right edge, within margin
    [InlineData(BarWidth - 8 - 40)]             // right cluster
    public void ControlColumns_FallThroughToTheWebView(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));

    [Theory]
    [InlineData(ClusterLeft + 64 + 2.4 + 4 + 2)] // right of the cluster + margin
    [InlineData(250)]                            // open bar left of the history arrows
    [InlineData(PageSettingsLeft + PageSettingsClusterWidth + 4 + 2)] // right of the mode-toggle+gear cluster + margin
    public void OpenBar_OutsideControlColumns_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));

    // At w=1100 the toggle is w - 1000 = 100px wide (below its 220 max), so
    // its carve-out and the drag zone beyond it both shrink to match.
    private const double MidWidth = 1100;
    private const double MidToggleWidth = MidWidth - 1000;
    private const double MidPageSettingsLeft = MidWidth / 2 + 460 / 2.0 + 7;
    private const double MidPageSettingsClusterWidth = MidToggleWidth + 4.8 + 34;

    [Theory]
    [InlineData(MidPageSettingsLeft + 50)]                              // inside the reduced mode-toggle column
    [InlineData(MidPageSettingsLeft + MidPageSettingsClusterWidth)]     // gear right edge, within margin
    public void MidWidth_ReducedToggleColumn_FallsThrough(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, MidWidth));

    [Theory]
    [InlineData(MidPageSettingsLeft + 220)] // inside the old fixed 220px toggle carve; the shrunk toggle ends well before this
    public void MidWidth_BeyondReducedToggleColumn_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, MidWidth));

    // Below 1060px the mode toggle is display:none (zero width, no gap); only
    // the gear survives in the page-settings cluster.
    private const double NarrowWidth = 1040;
    private const double NarrowPageSettingsLeft = NarrowWidth / 2 + 460 / 2.0 + 7;

    [Theory]
    [InlineData(NarrowPageSettingsLeft + 10)]  // gear button
    public void NarrowWidth_ToggleAbsent_GearOnlyFallsThrough(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, NarrowWidth));

    [Theory]
    [InlineData(NarrowPageSettingsLeft + 100)] // inside the old widened (mode-toggle-present) zone; toggle is gone, so this now drags
    public void NarrowWidth_OldWidenedZone_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, NarrowWidth));
}
