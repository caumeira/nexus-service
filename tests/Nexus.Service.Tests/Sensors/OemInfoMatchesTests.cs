using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class OemInfoMatchesTests
{
    [Theory]
    [InlineData("iBUYPOWER", "iBUYPOWER", true)]
    [InlineData("ibuypower", "iBUYPOWER", true)]
    [InlineData("  iBUYPOWER  ", "iBUYPOWER", true)]
    [InlineData("Gigabyte", "iBUYPOWER", false)]
    [InlineData("iBUYPOWER Inc.", "iBUYPOWER", false)]
    [InlineData(null, "iBUYPOWER", false)]
    [InlineData("", "iBUYPOWER", false)]
    [InlineData("   ", "iBUYPOWER", false)]
    public void Matches_DetectedAgainstSingleCandidate(string? detected, string candidate, bool expected)
    {
        Assert.Equal(expected, OemInfo.Matches(detected, new[] { candidate }));
    }

    [Fact]
    public void Matches_AnyOfSeveralCandidates()
    {
        Assert.True(OemInfo.Matches("iBUYPOWER", new[] { "HYTE", "iBUYPOWER" }));
        Assert.False(OemInfo.Matches("HP", new[] { "HYTE", "iBUYPOWER" }));
    }

    [Fact]
    public void Matches_NoCandidates_IsFalse()
    {
        Assert.False(OemInfo.Matches("iBUYPOWER", System.Array.Empty<string>()));
    }
}
