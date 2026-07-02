using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Nexus.Service.Net;

// Helpers for the panel phone pairing QR flow: resolve a LAN-reachable IP for
// the device and render a QR-code data URL.
internal static class LocalNetwork
{
    public static string GetLocalIp()
    {
        try
        {
            var candidates = new List<(string Ip, bool HasGateway)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                var hasGateway = HasDefaultGateway(props);

                foreach (var address in props.UnicastAddresses)
                {
                    var ip = address.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip) &&
                        !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        candidates.Add((ip.ToString(), hasGateway));
                    }
                }
            }

            var best = SelectPreferredLanIp(candidates);
            if (best is not null)
                return best;
        }
        catch { }

        return "localhost";
    }

    /// <summary>
    /// Picks the address a LAN phone can route to. Interfaces that own a
    /// default gateway (the physical LAN) outrank virtual/VPN adapters
    /// (Tailscale, OpenVPN, WSL, Hyper-V), which carry a host route but no
    /// gateway. CGNAT (100.64/10) is only a tie-breaker, never an exclusion -
    /// Tailscale lives there, but some ISPs hand it out on the real WAN.
    /// Returns null when no candidate qualifies.
    /// </summary>
    internal static string? SelectPreferredLanIp(IReadOnlyList<(string Ip, bool HasGateway)> candidates)
    {
        string? best = null;
        bool bestGateway = false, bestCgnat = false;
        foreach (var (ip, hasGateway) in candidates)
        {
            var cgnat = IsCgnat(ip);
            var better = best is null
                || (hasGateway && !bestGateway)
                || (hasGateway == bestGateway && bestCgnat && !cgnat);
            if (better)
            {
                best = ip;
                bestGateway = hasGateway;
                bestCgnat = cgnat;
            }
        }
        return best;
    }

    private static bool HasDefaultGateway(IPInterfaceProperties props)
    {
        foreach (var gw in props.GatewayAddresses)
        {
            if (gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gw.Address.Equals(IPAddress.Any))
            {
                return true;
            }
        }
        return false;
    }

    // 100.64.0.0/10 - CGNAT, where Tailscale's tailnet addresses live.
    private static bool IsCgnat(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static string GenerateQrSvgDataUrl(string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(5, "#000000", "#ffffff", drawQuietZones: true);
        return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
    }
}
