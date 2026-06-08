using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart.Discovery;

/// <summary>
/// Façade over the LAN discovery transports drivers use to find devices. Today
/// it exposes mDNS; SSDP and UDP-broadcast helpers slot in here as later brands
/// (Yeelight, LIFX, WiZ, Twinkly) need them, so drivers depend on one type.
/// </summary>
public sealed class LanDiscovery
{
    private readonly MdnsQuery _mdns;

    public LanDiscovery(MdnsQuery mdns) => _mdns = mdns;

    /// <summary>IPv4 hosts advertising a DNS-SD service type (e.g. "_hue._tcp").</summary>
    public Task<IReadOnlyList<string>> MdnsHostsAsync(string serviceType, int timeoutMs, CancellationToken ct)
        => _mdns.BrowseHostsAsync(serviceType, timeoutMs, ct);
}
