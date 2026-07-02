using System.Collections.Generic;
using Nexus.Service.Net;
using Xunit;

namespace Nexus.Service.Tests;

public class LocalNetworkTests
{
    // The reported bug: with Tailscale up, pairing advertised the tailnet
    // 100.x address a LAN phone can't route to. The physical LAN NIC owns a
    // default gateway; Tailscale's WinTun carries a host route but none.
    [Fact]
    public void Prefers_Gateway_Owning_Lan_Over_Tailscale()
    {
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("100.74.32.47", false),   // Tailscale, no gateway
            ("192.168.10.5", true),    // Ethernet, has gateway
        });

        Assert.Equal("192.168.10.5", chosen);
    }

    // Enumeration order must not decide it - the gateway signal must win even
    // when the VPN adapter is listed first (the observed Windows ordering).
    [Fact]
    public void Gateway_Wins_Regardless_Of_Order()
    {
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("192.168.10.5", true),
            ("100.74.32.47", false),
        });

        Assert.Equal("192.168.10.5", chosen);
    }

    [Fact]
    public void Returns_Null_When_No_Candidates()
    {
        Assert.Null(LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>()));
    }

    // No physical NIC with a gateway (isolated LAN / VPN-only) still yields an
    // address rather than falling through to "localhost".
    [Fact]
    public void Falls_Back_To_Only_Candidate_When_None_Have_Gateway()
    {
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("100.74.32.47", false),
        });

        Assert.Equal("100.74.32.47", chosen);
    }

    // CGNAT is a tie-breaker, not an exclusion: among gateway-owning NICs the
    // non-CGNAT one wins, but a lone gateway-owning CGNAT WAN is still used.
    [Fact]
    public void Non_Cgnat_Breaks_Tie_Among_Gateway_Owning()
    {
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("100.100.0.9", true),     // CGNAT WAN, has gateway
            ("192.168.1.20", true),    // private LAN, has gateway
        });

        Assert.Equal("192.168.1.20", chosen);
    }

    [Fact]
    public void Gateway_Cgnat_Beats_Non_Gateway_Private()
    {
        // A gateway-owning CGNAT WAN outranks a gateway-less private VPN route:
        // the default-route signal dominates the CGNAT tie-breaker.
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("10.2.0.3", false),       // VPN host route, no gateway
            ("100.100.0.9", true),     // CGNAT WAN, has gateway
        });

        Assert.Equal("100.100.0.9", chosen);
    }

    [Fact]
    public void Single_Lan_Interface_Is_Unchanged()
    {
        var chosen = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("192.168.10.5", true),
        });

        Assert.Equal("192.168.10.5", chosen);
    }

    // Rank-equal candidates keep first-seen order (strict "better" relation).
    // Guards against a >=-style regression that would let a later equal win.
    [Fact]
    public void Ties_Keep_First_Seen()
    {
        var bothGateway = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("192.168.10.5", true),
            ("192.168.20.5", true),
        });
        Assert.Equal("192.168.10.5", bothGateway);

        var bothGatewayless = LocalNetwork.SelectPreferredLanIp(new List<(string, bool)>
        {
            ("10.1.0.2", false),
            ("10.2.0.2", false),
        });
        Assert.Equal("10.1.0.2", bothGatewayless);
    }
}
