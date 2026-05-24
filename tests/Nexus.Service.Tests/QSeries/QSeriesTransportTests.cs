using System.Net;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class QSeriesTransportTests
{
    [Theory]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("192.168.1.42:5555", true)]
    [InlineData("10.0.0.1:5555", true)]
    [InlineData("", false)]
    public void IsTcpSerial_keys_off_colon_separator(string serial, bool expected)
    {
        Assert.Equal(expected, QSeriesTransport.IsTcpSerial(serial));
    }

    [Fact]
    public void ParseLanIPv4_extracts_src_from_ip_route_output()
    {
        // Real `ip route get 1.1.1.1` output from a Q60 over adb shell
        // (Android 11, MT8167, on a 192.168.1/24 home network).
        const string output = "1.1.1.1 dev wlan0 table 1023 src 192.168.1.42 uid 2000 \n    cache ";
        var ip = QSeriesTransport.ParseLanIPv4(output);
        Assert.NotNull(ip);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), ip);
    }

    [Fact]
    public void ParseLanIPv4_extracts_address_from_ip_addr_show_output()
    {
        // Trimmed `ip -4 addr show wlan0` output.
        const string output = """
            12: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc fq_codel state UP group default qlen 1000
                inet 192.168.4.7/24 brd 192.168.4.255 scope global wlan0
                   valid_lft 86397sec preferred_lft 86397sec
            """;
        var ip = QSeriesTransport.ParseLanIPv4(output);
        Assert.NotNull(ip);
        Assert.Equal(IPAddress.Parse("192.168.4.7"), ip);
    }

    [Fact]
    public void ParseLanIPv4_extracts_address_from_getprop_output()
    {
        // `getprop dhcp.wlan0.ipaddress` returns just the IP, possibly
        // with a trailing newline from the shell.
        const string output = "192.168.0.123\n";
        var ip = QSeriesTransport.ParseLanIPv4(output);
        Assert.NotNull(ip);
        Assert.Equal(IPAddress.Parse("192.168.0.123"), ip);
    }

    [Fact]
    public void ParseLanIPv4_skips_loopback()
    {
        const string output = "1.1.1.1 dev lo src 127.0.0.1 uid 2000";
        Assert.Null(QSeriesTransport.ParseLanIPv4(output));
    }

    [Fact]
    public void ParseLanIPv4_skips_link_local_169_254()
    {
        // No DHCP lease yet — kernel assigns 169.254.x.x. We should not
        // promote to TCP using a link-local address because it won't be
        // routable from the host.
        const string output = "1.1.1.1 dev wlan0 src 169.254.43.10 uid 2000";
        Assert.Null(QSeriesTransport.ParseLanIPv4(output));
    }

    [Fact]
    public void ParseLanIPv4_skips_unspecified_zero()
    {
        const string output = "0.0.0.0";
        Assert.Null(QSeriesTransport.ParseLanIPv4(output));
    }

    [Fact]
    public void ParseLanIPv4_returns_null_for_garbage_or_empty()
    {
        Assert.Null(QSeriesTransport.ParseLanIPv4(""));
        Assert.Null(QSeriesTransport.ParseLanIPv4(null));
        Assert.Null(QSeriesTransport.ParseLanIPv4("Cannot find device"));
        Assert.Null(QSeriesTransport.ParseLanIPv4("error: device 'X' not found"));
    }

    [Fact]
    public void ParseLanIPv4_skips_ip_route_marker_and_returns_src()
    {
        // `ip route get 1.1.1.1` echoes the target IP first before the
        // src address. The parser must walk past the public 1.1.1.1
        // (rejected by the RFC-1918 gate) and return the actual LAN src.
        const string output = "1.1.1.1 dev wlan0 src 192.168.1.42 uid 0";
        Assert.Equal(IPAddress.Parse("192.168.1.42"), QSeriesTransport.ParseLanIPv4(output));
    }

    [Fact]
    public void IsPrivateLanIPv4_accepts_rfc1918_ranges()
    {
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("192.168.1.1")));
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("10.0.0.1")));
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("172.16.0.1")));
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("172.31.255.254")));
    }

    [Fact]
    public void IsPrivateLanIPv4_accepts_cgnat_range()
    {
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("100.64.0.1")));
        Assert.True(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("100.127.255.254")));
    }

    [Fact]
    public void IsPrivateLanIPv4_rejects_172_outside_16_31_range()
    {
        // 172.32+ is public space, only 172.16/12 is private.
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("172.15.0.1")));
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("172.32.0.1")));
    }

    [Fact]
    public void IsPrivateLanIPv4_rejects_public_loopback_and_link_local()
    {
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("1.1.1.1")));
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("8.8.8.8")));
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("127.0.0.1")));
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void IsPrivateLanIPv4_rejects_ipv6()
    {
        // IPv6 addresses are not in scope for adb tcpip — the protocol
        // is IPv4-only in adbd. Make sure the gate rejects them.
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("::1")));
        Assert.False(QSeriesTransport.IsPrivateLanIPv4(IPAddress.Parse("fe80::1")));
    }
}
