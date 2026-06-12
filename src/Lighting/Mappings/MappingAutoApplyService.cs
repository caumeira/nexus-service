using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// First-seen community mapping auto-apply - the "it just works" path.
/// Periodically diffs the connected lighting devices against the known-device
/// list; for genuinely new hardware it asks the registry for the top mapping
/// and applies it silently when the registry's eligibility gate allows,
/// announcing via a toast with one-click undo. Also emits the
/// devices-seen ownership signal that feeds the registry's provenance and
/// qualified-adoption gates.
///
/// Guard rails, all local:
///   - never touches a device with ANY user layout data (mapping, overrides,
///     groups) or a prior undo veto
///   - no-op rule: an artifact whose content hash equals the current
///     resolved layout is recorded as known and skipped - no apply, no
///     adoption row
///   - every artifact passes the local lint before it reaches the engine
/// </summary>
public sealed class MappingAutoApplyService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IConfigStore _store;
    private readonly MappingCloudClient _cloud;
    private readonly MappingApplyService _apply;
    private readonly ILightingDeviceProvider _devices;
    private readonly Nexus.Service.Lighting.Zones.ZoneTopology _topology;
    private readonly MultiplexHub _hub;

    public MappingAutoApplyService(
        IConfigStore store,
        MappingCloudClient cloud,
        MappingApplyService apply,
        ILightingDeviceProvider devices,
        Nexus.Service.Lighting.Zones.ZoneTopology topology,
        MultiplexHub hub)
    {
        _store = store;
        _cloud = cloud;
        _apply = apply;
        _devices = devices;
        _topology = topology;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mappings] auto-apply tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return false; }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var cards = _devices.GetAll().Devices;
        if (cards.Count == 0)
            return;
        var settings = _store.Load();

        var newDevices = new List<Models.Devices.LightingDevice>();
        var resolvedIds = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card.DeviceKey.Length > 0)
                seenKeys.Add(card.DeviceKey);
            if (settings.Devices.MappingKnownDevices.Contains(card.Id))
                continue;
            // Keyless devices and devices the user already laid out have
            // nothing pending; everything else only becomes "known" once a
            // registry lookup actually completed, so a transient outage on
            // the first-seen tick does not consume the one auto-apply shot.
            if (card.DeviceKey.Length == 0 || HasLocalLayoutData(settings, card.Id))
                resolvedIds.Add(card.Id);
            else
                newDevices.Add(card);
        }

        // Ownership signal (no-ops when anonymous data is off).
        await _cloud.ReportDevicesSeenAsync(new List<string>(seenKeys), ct).ConfigureAwait(false);

        foreach (var card in newDevices)
        {
            if (await TryAutoApplyAsync(card, ct).ConfigureAwait(false))
                resolvedIds.Add(card.Id);
        }

        if (resolvedIds.Count > 0)
        {
            _store.Update(s =>
            {
                foreach (var id in resolvedIds)
                {
                    if (!s.Devices.MappingKnownDevices.Contains(id))
                        s.Devices.MappingKnownDevices.Add(id);
                }
            });
        }
    }

    private bool HasLocalLayoutData(NexusSettings settings, string id)
        => settings.Devices.AppliedMappings.ContainsKey(id)
            || _topology.HasUserOverrides(id, settings)
            || settings.Devices.LedGroups.ContainsKey(id)
            || settings.Devices.MappingAutoApplyDeclined.Contains(id);

    /// <summary>Returns true when the registry interaction completed (device becomes known); false on outage so the next tick retries.</summary>
    private async Task<bool> TryAutoApplyAsync(Models.Devices.LightingDevice card, CancellationToken ct)
    {
        var list = await _cloud.GetMappingsAsync(card.DeviceKey, forceRefresh: false, ct).ConfigureAwait(false);
        if (list.Offline)
            return false;
        Models.Devices.CommunityMapping? top = null;
        foreach (var item in list.Items)
        {
            if (item.AutoApply && item.Payload is not null)
            { top = item; break; }
        }
        if (top?.Payload is null)
            return true;

        // No-op rule: identical to the layout the device already resolves to
        // means nothing is applied and no adoption row exists. The common
        // case (registry default == shipped default) costs the backend
        // nothing.
        var current = _apply.Export(card.Id);
        if (current is not null && MappingHash.ContentHash(current) == top.ContentHash)
            return true;

        if (_apply.Apply(card.Id, top.Payload, top.Id, "community", auto: true, top.ContentHash)
            != MappingApplyService.ApplyOutcome.Applied)
        {
            return true;
        }

        Console.WriteLine($"[mappings] auto-applied '{top.Name}' to {card.Name} ({top.AdopterCount} adopters)");
        PanelTopics.BroadcastMappingApplied(_hub, new MappingAutoAppliedFrame
        {
            DeviceId = card.Id,
            DeviceName = card.Name,
            MappingId = top.Id,
            MappingName = top.Name,
            AdopterCount = top.AdopterCount,
        });
        return true;
    }
}
