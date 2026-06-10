#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Sockets;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Bridges the helper's WM_DISPLAYCHANGE push (<c>displays.changed</c>
/// envelopes) to the <c>displays</c> multiplex topic. A short quiet-period
/// timer coalesces the burst Windows fires during a single arrangement
/// change. Helper connect/disconnect also rebroadcasts: topology flips
/// between known and unknown at those edges.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayTopologyWatcher : BackgroundService
{
    private const int DebounceMs = 500;

    private readonly HelperRegistry _helpers;
    private readonly MultiplexHub _hub;
    private readonly Timer _debounce;

    public DisplayTopologyWatcher(HelperRegistry helpers, MultiplexHub hub)
    {
        _helpers = helpers;
        _hub = hub;
        _debounce = new Timer(_ => Broadcast(), null, Timeout.Infinite, Timeout.Infinite);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _helpers.InboundEnvelope += OnEnvelope;
        _helpers.Connected += OnHelperEdge;
        _helpers.Disconnected += OnHelperEdge;
        stoppingToken.Register(() =>
        {
            _helpers.InboundEnvelope -= OnEnvelope;
            _helpers.Connected -= OnHelperEdge;
            _helpers.Disconnected -= OnHelperEdge;
            _debounce.Dispose();
        });
        return Task.CompletedTask;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type == DisplayTopologyCommands.ChangedType) Kick();
    }

    private void OnHelperEdge(HelperConnection _) => Kick();

    private void Kick()
    {
        try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Broadcast()
    {
        try { PanelTopics.BroadcastDisplays(_hub); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays] topology broadcast failed: {ex.Message}");
        }
    }
}
#endif
