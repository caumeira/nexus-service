using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class LinuxNetworkProviderTests
{
    // /proc/net/tcp stores IPv4 little-endian, so 127.0.0.1 is "0100007F".
    [Theory]
    [InlineData("01010101:01BB", true)]   // 1.1.1.1:443 - real peer
    [InlineData("08080808:0050", true)]   // 8.8.8.8:80
    [InlineData("0100007F:1F90", false)]  // 127.0.0.1 - loopback
    [InlineData("0101007F:0035", false)]  // 127.0.1.1 - loopback (/8)
    [InlineData("00000000:0000", false)]  // 0.0.0.0 - unconnected
    public void IsRemoteRoutable_ipv4(string remHex, bool expected)
        => Assert.Equal(expected, LinuxNetworkProvider.IsRemoteRoutable(remHex));

    [Theory]
    [InlineData("00000000000000000000000001000000:0050", false)] // ::1 loopback
    [InlineData("00000000000000000000000000000000:0000", false)] // :: unconnected
    [InlineData("0000000000000000FFFF00000101010A:01BB", true)]  // a real v6 peer
    public void IsRemoteRoutable_ipv6(string remHex, bool expected)
        => Assert.Equal(expected, LinuxNetworkProvider.IsRemoteRoutable(remHex));
}
