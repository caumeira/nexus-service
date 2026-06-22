using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests;

/// <summary>
/// Network provider parsing logic. <see cref="MacNetworkProvider.ParseNettop"/>
/// parses nettop CSV output and merges by process name;
/// <see cref="NetstatParser"/> derives internet-active PIDs from netstat output.
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

    // --- NetstatParser (Windows) ---

    [Theory]
    [InlineData("127.0.0.1:9400")]
    [InlineData("127.255.255.255:80")]
    [InlineData("0.0.0.0:0")]
    [InlineData("[::1]:443")]
    [InlineData("[::]:0")]
    [InlineData("*:*")]
    [InlineData("[::ffff:127.0.0.1]:1234")]
    public void IsLoopbackOrUnspecified_True(string foreignAddress)
    {
        Assert.True(NetstatParser.IsLoopbackOrUnspecified(foreignAddress));
    }

    [Theory]
    [InlineData("142.250.80.46:443")]      // public IPv4
    [InlineData("192.168.1.235:9400")]     // LAN (still real wire traffic)
    [InlineData("8.8.8.8:53")]
    [InlineData("[2606:4700::1]:443")]     // public IPv6
    [InlineData("[fe80::1]:5353")]         // IPv6 link-local
    public void IsLoopbackOrUnspecified_False(string foreignAddress)
    {
        Assert.False(NetstatParser.IsLoopbackOrUnspecified(foreignAddress));
    }

    [Fact]
    public void ParseInternetActivePids_ExcludesLoopbackOnlyPids()
    {
        // nexus-service (PID 9876) only talks to localhost.
        // Edge browser (PID 4242) has both a loopback handshake AND public traffic.
        var output = """
        Active Connections

          Proto  Local Address          Foreign Address        State           PID
          TCP    127.0.0.1:9400         127.0.0.1:50123        ESTABLISHED     9876
          TCP    127.0.0.1:9400         127.0.0.1:50124        ESTABLISHED     9876
          TCP    127.0.0.1:50123        127.0.0.1:9400         ESTABLISHED     4242
          TCP    192.168.1.35:50500     142.250.80.46:443      ESTABLISHED     4242
          TCP    0.0.0.0:9400           0.0.0.0:0              LISTENING       9876
          UDP    0.0.0.0:5353           *:*                                    1111
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Contains(4242, pids);     // Edge has at least one real peer
        Assert.DoesNotContain(9876, pids); // nexus-service is loopback-only
        Assert.DoesNotContain(1111, pids); // mDNS listener has no real peer
    }

    [Fact]
    public void ParseInternetActivePids_HandlesIpv6Peers()
    {
        var output = """
          TCP    [::1]:9400             [::1]:50123            ESTABLISHED     9876
          TCP    [2001:db8::1]:50500    [2606:4700::1]:443     ESTABLISHED     4242
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Contains(4242, pids);
        Assert.DoesNotContain(9876, pids);
    }

    [Fact]
    public void ParseInternetActivePids_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(NetstatParser.ParseInternetActivePids(""));
        Assert.Empty(NetstatParser.ParseInternetActivePids("Active Connections\n"));
    }

    [Fact]
    public void ParseInternetActivePids_IgnoresKernelPid()
    {
        var output = """
          TCP    192.168.1.35:445       142.250.80.46:443      ESTABLISHED     0
          TCP    192.168.1.35:50500     142.250.80.46:443      ESTABLISHED     4242
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Single(pids);
        Assert.Contains(4242, pids);
    }
}
