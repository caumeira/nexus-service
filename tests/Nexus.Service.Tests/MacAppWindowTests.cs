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
    private const double BarWidth = 1200;
    // .topBar padding-left + --mac-traffic-light-inset.
    private const double ClusterLeft = 8 + 96;

    [Theory]
    [InlineData(ClusterLeft + 16)]              // collapse toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 16)]   // Focus toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 30)]   // Focus toggle right edge
    [InlineData(BarWidth / 2)]                  // search pill center
    [InlineData(BarWidth - 8 - 40)]             // right cluster
    public void ControlColumns_FallThroughToTheWebView(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));

    [Theory]
    [InlineData(ClusterLeft + 64 + 2.4 + 4 + 2)] // right of the cluster + margin
    [InlineData(250)]                            // open bar left of the history arrows
    public void OpenBar_OutsideControlColumns_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));
}
