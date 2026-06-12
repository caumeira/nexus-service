using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

public class PortalAccentTests
{
    [Theory]
    // KDE Plasma 6 blue accent (the live value read off the Bazzite box).
    [InlineData("(<(0.33725491166114807, 0.62352943420410156, 0.80000001192092896)>,)", "#569FCC")]
    [InlineData("(<(1.0, 0.0, 0.5)>,)", "#FF0080")]
    [InlineData("(<(0.0, 0.0, 0.0)>,)", "#000000")]
    public void ParsesPortalTuple(string gdbus, string expected)
        => Assert.Equal(expected, PortalAccent.Parse(gdbus));

    [Theory]
    // (-1,-1,-1) is the portal's "no accent set" sentinel.
    [InlineData("(<(-1.0, -1.0, -1.0)>,)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a tuple")]
    [InlineData("(<(0.5, 0.5)>,)")]
    public void ReturnsNullWhenAbsentOrInvalid(string gdbus)
        => Assert.Null(PortalAccent.Parse(gdbus));

    [Fact]
    public void ReturnsNullForNull()
        => Assert.Null(PortalAccent.Parse(null));
}
