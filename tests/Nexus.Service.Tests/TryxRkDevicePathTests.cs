using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRkDevicePathTests
{
    private const string SePath = @"\\?\USB#VID_391A&PID_1021#c30c0d9deab02fd2#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";

    [Fact]
    public void ParseSerial_ReturnsThirdSegment()
    {
        Assert.Equal("c30c0d9deab02fd2", TryxRkDevicePath.ParseSerial(SePath));
    }

    [Fact]
    public void ParseSerial_ReturnsEmpty_WhenNoSerialSegment()
    {
        Assert.Equal("", TryxRkDevicePath.ParseSerial(@"\\?\USB#VID_391A&PID_1021"));
    }

    [Theory]
    [InlineData("PID_1011", 0x1011)]
    [InlineData("PID_1021", 0x1021)]
    [InlineData("PID_10A1", 0x10A1)]
    [InlineData("PID_10B1", 0x10B1)]
    public void ParseProductId_ReadsRealPidFromPath(string pidToken, int expected)
    {
        var path = SePath.Replace("PID_1021", pidToken);
        Assert.Equal(expected, TryxRkDevicePath.ParseProductId(path, TryxPanoramaProtocol.ProductIdPanoramaRk));
    }

    [Fact]
    public void ParseProductId_IsCaseInsensitive_IncludingLowercaseHexLetters()
    {
        Assert.Equal(0x10AB, TryxRkDevicePath.ParseProductId(
            @"\\?\usb#vid_391a&pid_10ab#serial#{guid}", TryxPanoramaProtocol.ProductIdPanoramaRk));
    }

    [Fact]
    public void ParseProductId_ReturnsFallback_WhenPidWindowIsNotHex()
    {
        // Four chars follow "PID_" so the length guard passes; the '#' makes TryParse fail.
        Assert.Equal(0x1011, TryxRkDevicePath.ParseProductId(@"\\?\USB#VID_391A&PID_10#s", 0x1011));
    }

    [Fact]
    public void ParseProductId_ReturnsFallback_WhenNoPidToken()
    {
        Assert.Equal(0x1011, TryxRkDevicePath.ParseProductId(@"\\?\USB#VID_391A", 0x1011));
    }

    [Fact]
    public void ParseProductId_ReturnsFallback_WhenPidTokenTruncated()
    {
        Assert.Equal(0x1011, TryxRkDevicePath.ParseProductId(@"\\?\USB#VID_391A&PID_10", 0x1011));
    }
}
