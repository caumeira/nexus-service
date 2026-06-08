using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart.Discovery;

/// <summary>
/// Minimal, dependency-free one-shot mDNS browser. We only need to *query* (not
/// advertise), so this builds a single PTR question for a service type and
/// collects the source addresses of the responders. The unicast-response (QU)
/// bit asks devices to reply directly to our ephemeral port, which sidesteps
/// multicast-group-join portability headaches across Windows/macOS/Linux.
///
/// Every responder to a service-specific query offers that service, so a reply
/// source address is a device host. We don't parse A/SRV records — the source
/// IP is what we want and is reliable. Best-effort: any socket error yields an
/// empty list (callers also have cloud discovery + manual IP).
/// </summary>
public sealed class MdnsQuery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Browse a DNS-SD service type (e.g. "_hue._tcp") and return the
    /// distinct IPv4 hosts that responded.</summary>
    public async Task<IReadOnlyList<string>> BrowseHostsAsync(string serviceType, int timeoutMs, CancellationToken ct)
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

            var query = BuildPtrQuery(serviceType);
            await sock.SendToAsync(query, SocketFlags.None, MulticastEndpoint, ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeoutMs);
            var buf = new byte[4096];
            while (!deadline.IsCancellationRequested)
            {
                SocketReceiveFromResult res;
                try
                {
                    res = await sock.ReceiveFromAsync(buf, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                if (res.ReceivedBytes > 0 && res.RemoteEndPoint is IPEndPoint ep
                    && ep.AddressFamily == AddressFamily.InterNetwork)
                {
                    hosts.Add(ep.Address.ToString());
                }
            }
        }
        catch
        {
            // Best-effort discovery — never throw to callers.
        }
        return new List<string>(hosts);
    }

    /// <summary>Build a standard DNS query: one PTR question for
    /// "&lt;serviceType&gt;.local" with the QU (unicast response) bit set.</summary>
    private static byte[] BuildPtrQuery(string serviceType)
    {
        // Labels: each dot-separated part of "_hue._tcp" + "local".
        var name = serviceType.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? serviceType
            : serviceType + ".local";
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var body = new List<byte>(32)
        {
            0x00, 0x00, // ID (mDNS: 0)
            0x00, 0x00, // flags: standard query
            0x00, 0x01, // QDCOUNT = 1
            0x00, 0x00, // ANCOUNT
            0x00, 0x00, // NSCOUNT
            0x00, 0x00, // ARCOUNT
        };
        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }
        body.Add(0x00);       // name terminator
        body.Add(0x00); body.Add(0x0C); // QTYPE = PTR (12)
        body.Add(0x80); body.Add(0x01); // QCLASS = IN (1) | QU unicast-response bit (0x8000)
        return body.ToArray();
    }
}
