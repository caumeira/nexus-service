using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Sockets;

namespace Nexus.Service.Net;

/// <summary>
/// Nudges the dashboard to re-mint the pairing QR whenever the host's IP
/// changes (VPN toggle, Wi-Fi↔wired switch, DHCP renew). A pairing QR embeds
/// the LAN IP picked at mint time (<see cref="LocalNetwork.GetLocalIp"/>), so
/// without this nudge a displayed QR keeps advertising a stale address until
/// its 60s TTL elapses. The push is content-less: subscribers re-fetch
/// <c>GET /panel/phone/pair-qr</c>, which re-reads the current address.
///
/// <see cref="NetworkChange.NetworkAddressChanged"/> fires in bursts (one per
/// interface address add/remove), so the broadcast is debounced - a burst
/// coalesces into a single nudge a short beat after the last event, by which
/// point the new address has settled. The timer coalesces events; it is not a
/// sleep papering over a race.
/// </summary>
public sealed class NetworkAddressChangeListener : BackgroundService
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(750);

    private readonly MultiplexHub _hub;
    private readonly ILogger<NetworkAddressChangeListener> _logger;
    private readonly Timer _debounce;
    private NetworkAddressChangedEventHandler? _handler;

    public NetworkAddressChangeListener(MultiplexHub hub, ILogger<NetworkAddressChangeListener> logger)
    {
        _hub = hub;
        _logger = logger;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe and return - the OS raises the event on its own thread; the
        // handler just (re)arms the debounce timer. Nothing here blocks startup.
        _handler = (_, _) => _debounce.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        NetworkChange.NetworkAddressChanged += _handler;
        return Task.CompletedTask;
    }

    private void Fire()
    {
        try
        {
            PanelTopics.BroadcastPairQrRefresh(_hub);
        }
        catch (Exception ex)
        {
            // Best-effort nudge: the QR still re-mints at its TTL.
            _logger.LogWarning(ex, "Failed to broadcast pairing-QR refresh on network change.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null)
            NetworkChange.NetworkAddressChanged -= _handler;
        _debounce.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
