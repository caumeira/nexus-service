using Nexus.Service.Peripherals.Hyte.Cnvs;

namespace Nexus.Service.Tests.Cnvs;

public class CnvsFirmwareTests
{
    [Theory]
    [InlineData("1.0.2.1", true)]   // documented minimum that adds FF DC 07/08
    [InlineData("1.0.2.2", true)]   // second hex shipped to the same feature
    [InlineData("1.0.2.5", true)]   // higher build, same minor
    [InlineData("1.0.3.0", true)]   // higher minor
    [InlineData("2.0.0.0", true)]   // higher major
    [InlineData("1.0.1.1", false)]  // Y70 dev unit pre-flash — silent no-op
    [InlineData("1.0.0.99", false)] // older build
    [InlineData("0.9.9.9", false)]  // lower major
    public void SupportsSettings_matches_protocol_doc_v1_0_2_1_floor(string version, bool expected)
    {
        Assert.Equal(expected, CnvsFirmware.SupportsSettings(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1")]      // too few parts
    [InlineData("1.0")]    // still too few
    [InlineData("a.b.c")]  // non-numeric
    public void SupportsSettings_returns_false_on_unknown_or_unparseable(string? version)
    {
        // Conservative default: UI hides the feature when we can't tell what's connected.
        Assert.False(CnvsFirmware.SupportsSettings(version));
    }

    [Fact]
    public void SupportsSettings_ignores_hardware_byte()
    {
        // Three-part forms are valid; hardware byte (4th) is irrelevant to the gate.
        Assert.True(CnvsFirmware.SupportsSettings("1.0.2"));
        Assert.True(CnvsFirmware.SupportsSettings("1.0.2.1"));
        Assert.True(CnvsFirmware.SupportsSettings("1.0.2.99"));
    }
}
