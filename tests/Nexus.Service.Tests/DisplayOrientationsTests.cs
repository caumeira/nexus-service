using Nexus.Service.Models.Displays;

namespace Nexus.Service.Tests;

public class DisplayOrientationsTests
{
    [Theory]
    [InlineData(DisplayOrientations.Landscape, 0)]
    [InlineData(DisplayOrientations.Portrait, 90)]
    [InlineData(DisplayOrientations.LandscapeFlipped, 180)]
    [InlineData(DisplayOrientations.PortraitFlipped, 270)]
    public void Mac_degrees_round_trip(string orientation, int degrees)
    {
        Assert.Equal(degrees, DisplayOrientations.ToMacDegrees(orientation));
        Assert.Equal(orientation, DisplayOrientations.FromMacDegrees(degrees));
    }

    [Theory]
    [InlineData(360.0, DisplayOrientations.Landscape)]
    [InlineData(-90.0, DisplayOrientations.PortraitFlipped)]
    [InlineData(89.6, DisplayOrientations.Portrait)]
    [InlineData(45.0, "")]
    public void From_mac_degrees_normalizes_and_rejects_non_quadrants(double degrees, string expected)
        => Assert.Equal(expected, DisplayOrientations.FromMacDegrees(degrees));

    [Fact]
    public void To_mac_degrees_rejects_unknown_orientation()
        => Assert.Null(DisplayOrientations.ToMacDegrees("Sideways"));
}
