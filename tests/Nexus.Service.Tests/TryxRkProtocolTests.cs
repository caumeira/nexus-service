using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRkProtocolTests
{
    // Byte-for-byte camera-verified reference frames, captured against the physical
    // RK-firmware Panorama panel (VID 0x391A). Any change to these bytes must be
    // re-verified against real hardware, not derived from the protobuf writer.

    [Fact]
    public void BuildHeartbeat_matches_camera_verified_frame()
    {
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0c, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0x52, 0x08, 0x0a, 0x06, 0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x3f,
        };

        var actual = TryxRkProtocol.BuildHeartbeat();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildBrightness_100_matches_camera_verified_frame()
    {
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0b, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x06, 0x2a, 0x04, 0x08, 0x01, 0x10, 0x64,
        };

        var actual = TryxRkProtocol.BuildBrightness(100);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildBrightness_10_matches_camera_verified_frame()
    {
        byte[] expected =
        {
            0x54, 0x52, 0x59, 0x58, 0x0b, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0xc2, 0x0c, 0x06, 0x2a, 0x04, 0x08, 0x01, 0x10, 0x0a,
        };

        var actual = TryxRkProtocol.BuildBrightness(10);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(150, 100)]
    public void BuildBrightness_clamps_out_of_range_values(int input, int clamped)
    {
        var expected = TryxRkProtocol.BuildBrightness(clamped);

        var actual = TryxRkProtocol.BuildBrightness(input);

        Assert.Equal(expected, actual);
    }
}
