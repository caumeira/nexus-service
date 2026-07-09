using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests;

/// <summary>
/// Network provider parsing logic. <see cref="MacNetworkProvider.ParseNettop"/>
/// parses nettop CSV output and merges by process name;
/// <see cref="TcpPeerFilter"/> classifies TCP-table remote peers on Windows.
/// </summary>
public class NetworkParsingTests
{
    [Fact]
    public void ParseNettop_ExtractsFields()
    {
        var result = MacNetworkProvider.ParseNettop("Chrome.12345,1024,512\n");

        var chrome = Assert.Single(result);
        Assert.Equal("Chrome", chrome.Name);
        Assert.Equal(1024, chrome.BytesIn);
        Assert.Equal(512, chrome.BytesOut);
    }

    [Fact]
    public void ParseNettop_StripsPidSuffixFromProcessName()
    {
        var result = MacNetworkProvider.ParseNettop(
            "Chrome.12345,1,0\nSafari.67890,1,0\nnexus-service.999,1,0\n");

        Assert.Contains(result, e => e.Name == "Chrome");
        Assert.Contains(result, e => e.Name == "Safari");
        Assert.Contains(result, e => e.Name == "nexus-service");
    }

    [Fact]
    public void ParseNettop_PreservesNamesWithoutNumericSuffix()
    {
        var result = MacNetworkProvider.ParseNettop(
            "kernel_task,1,0\ncom.apple.WebKit.Networking,1,0\nnode.js,1,0\n");

        Assert.Contains(result, e => e.Name == "kernel_task");
        Assert.Contains(result, e => e.Name == "com.apple.WebKit.Networking");
        Assert.Contains(result, e => e.Name == "node.js");
    }

    [Fact]
    public void ParseNettop_CombinesBytesForSameProcess()
    {
        var result = MacNetworkProvider.ParseNettop(
            "Chrome.1,1000,500\nChrome.2,2000,300\nSafari.3,100,50\n");

        Assert.Equal(2, result.Count);
        var chrome = result.First(e => e.Name == "Chrome");
        Assert.Equal(3000, chrome.BytesIn);
        Assert.Equal(800, chrome.BytesOut);
    }

    [Fact]
    public void ParseNettop_SortsByTotalDescending()
    {
        var result = MacNetworkProvider.ParseNettop(
            "Slack.1,100,50\nChrome.2,5000,3000\nSpotify.3,2000,100\n");

        Assert.Equal("Chrome", result[0].Name);
        Assert.Equal("Spotify", result[1].Name);
        Assert.Equal("Slack", result[2].Name);
    }

    [Fact]
    public void ParseNettop_FiltersZeroTraffic()
    {
        var result = MacNetworkProvider.ParseNettop("Chrome.1,1000,500\nIdleProcess.2,0,0\n");

        var only = Assert.Single(result);
        Assert.Equal("Chrome", only.Name);
    }

    [Fact]
    public void ParseNettop_SkipsMalformedLines()
    {
        var result = MacNetworkProvider.ParseNettop(
            "Chrome.1234,10240,5120\nbadline\n,,,\nSafari.9999,512,256\n");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseNettop_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(MacNetworkProvider.ParseNettop(""));
        Assert.Empty(MacNetworkProvider.ParseNettop("   \n  \n"));
    }

    // --- TcpPeerFilter (Windows TCP connection table) ---

    [Theory]
    [InlineData(127, 0, 0, 1, false)]
    [InlineData(127, 255, 255, 255, false)]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(142, 250, 80, 46, true)]   // public IPv4
    [InlineData(192, 168, 1, 235, true)]   // LAN (still real wire traffic)
    [InlineData(8, 8, 8, 8, true)]
    public void IsInternetPeerV4_ClassifiesRemoteAddresses(byte a, byte b, byte c, byte d, bool expected)
    {
        // MIB_TCPROW_OWNER_PID.dwRemoteAddr is network byte order: the first
        // octet sits in the low byte on little-endian.
        var addr = (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
        Assert.Equal(expected, TcpPeerFilter.IsInternetPeerV4(addr));
    }

    [Fact]
    public void IsInternetPeerV6_LoopbackAndUnspecified_False()
    {
        var loopback = new byte[16];
        loopback[15] = 1; // ::1
        Assert.False(TcpPeerFilter.IsInternetPeerV6(loopback));

        Assert.False(TcpPeerFilter.IsInternetPeerV6(new byte[16])); // ::
    }

    [Fact]
    public void IsInternetPeerV6_V4MappedLoopbackAndUnspecified_False()
    {
        var mappedLoopback = new byte[16];
        mappedLoopback[10] = 0xFF;
        mappedLoopback[11] = 0xFF;
        mappedLoopback[12] = 127; // ::ffff:127.0.0.1
        mappedLoopback[15] = 1;
        Assert.False(TcpPeerFilter.IsInternetPeerV6(mappedLoopback));

        var mappedUnspecified = new byte[16];
        mappedUnspecified[10] = 0xFF;
        mappedUnspecified[11] = 0xFF; // ::ffff:0.0.0.0
        Assert.False(TcpPeerFilter.IsInternetPeerV6(mappedUnspecified));
    }

    [Fact]
    public void IsInternetPeerV6_PublicLinkLocalAndMappedLan_True()
    {
        var publicV6 = new byte[16];
        publicV6[0] = 0x26; // 2606:4700::1
        publicV6[1] = 0x06;
        publicV6[2] = 0x47;
        publicV6[15] = 1;
        Assert.True(TcpPeerFilter.IsInternetPeerV6(publicV6));

        var linkLocal = new byte[16];
        linkLocal[0] = 0xFE; // fe80::1
        linkLocal[1] = 0x80;
        linkLocal[15] = 1;
        Assert.True(TcpPeerFilter.IsInternetPeerV6(linkLocal));

        var mappedLan = new byte[16];
        mappedLan[10] = 0xFF;
        mappedLan[11] = 0xFF;
        mappedLan[12] = 192; // ::ffff:192.168.1.35
        mappedLan[13] = 168;
        mappedLan[14] = 1;
        mappedLan[15] = 35;
        Assert.True(TcpPeerFilter.IsInternetPeerV6(mappedLan));
    }

    [Fact]
    public void IsInternetPeerV6_WrongLength_False()
    {
        Assert.False(TcpPeerFilter.IsInternetPeerV6(new byte[4]));
        Assert.False(TcpPeerFilter.IsInternetPeerV6(System.ReadOnlySpan<byte>.Empty));
    }
}
