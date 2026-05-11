using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Qos.Service.Net;

// Helpers for the panel phone pairing QR flow: resolve a LAN-reachable IP for
// the device and render a QR-code data URL.
internal static class LocalNetwork
{
    public static string GetLocalIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip) &&
                        !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        return ip.ToString();
                    }
                }
            }
        }
        catch { }

        return "localhost";
    }

    public static string GenerateQrSvgDataUrl(string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(5, "#000000", "#ffffff", drawQuietZones: true);
        return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
    }
}
