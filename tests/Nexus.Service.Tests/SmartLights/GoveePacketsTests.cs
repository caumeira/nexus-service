using System;
using Nexus.Service.Lighting.Smart.Drivers.Govee;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>Byte-vector tests pinning the razer/DreamView wire format — the
/// known-good reference packets the hardware accepts.</summary>
public class GoveePacketsTests
{
    [Fact]
    public void RazerMode_matchesKnownReferencePackets()
    {
        // The canonical enable/disable strings every working implementation
        // (OpenRGB, SignalRGB) sends.
        Assert.Equal("uwABsQEK", GoveePackets.RazerModeBase64(enable: true));
        Assert.Equal("uwABsQAL", GoveePackets.RazerModeBase64(enable: false));

        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x01, 0x0A }, GoveePackets.BuildRazerMode(true));
        Assert.Equal(new byte[] { 0xBB, 0x00, 0x01, 0xB1, 0x00, 0x0B }, GoveePackets.BuildRazerMode(false));
    }

    [Fact]
    public void RazerFrame_packsHeaderColorsAndChecksum()
    {
        var rgb = new byte[] { 255, 0, 0, 0, 255, 0 };
        var pkt = GoveePackets.BuildRazerFrame(rgb, count: 2, scale01: 1f, gradient: true);

        Assert.Equal(new byte[]
        {
            0xBB,       // magic
            0x00, 0x08, // payload length = 2 + 3*2, big-endian
            0xB0,       // color-data command
            0x01,       // gradient flag
            0x02,       // color count
            0xFF, 0x00, 0x00,
            0x00, 0xFF, 0x00,
            0x00,       // XOR checksum over all preceding bytes
        }, pkt);
    }

    [Fact]
    public void RazerFrame_checksumIsXorOfAllPrecedingBytes()
    {
        var rgb = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var pkt = GoveePackets.BuildRazerFrame(rgb, count: 3, scale01: 1f, gradient: false);
        byte expected = 0;
        for (var i = 0; i < pkt.Length - 1; i++) expected ^= pkt[i];
        Assert.Equal(expected, pkt[^1]);
        Assert.Equal(0x00, pkt[4]); // gradient off
    }

    [Fact]
    public void RazerFrame_scalesColorsByBrightness()
    {
        var rgb = new byte[] { 255, 100, 0 };
        var pkt = GoveePackets.BuildRazerFrame(rgb, count: 1, scale01: 0.5f, gradient: true);
        Assert.Equal(128, pkt[6]);
        Assert.Equal(50, pkt[7]);
        Assert.Equal(0, pkt[8]);
    }

    [Fact]
    public void RazerFrame_clampsCountToAvailableColors()
    {
        var rgb = new byte[] { 10, 20, 30 }; // one color available
        var pkt = GoveePackets.BuildRazerFrame(rgb, count: 5, scale01: 1f, gradient: true);
        Assert.Equal(1, pkt[5]);
        Assert.Equal(4 + (2 + 3) + 1, pkt.Length);
    }
}
