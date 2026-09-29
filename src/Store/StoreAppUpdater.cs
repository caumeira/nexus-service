using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.FocusModes;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;
using Nexus.Service.Update;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Keeps every store-installed app on the catalog's newest version this build can run.
/// </summary>
/// <remarks>
/// Uses the Install button's rules: the entitled download when an account grants it,
/// and no account needed for an app whose hardware is attached. Every app in the user
/// root is a candidate, including a copy placed there by hand: the catalog is the
/// source of truth for an id it knows. An app the catalog does not know, or one
/// installed newer than the catalog, is left alone. An update that asks for a
/// capability the installed version lacks is held in <see cref="StorePendingUpdates"/>
/// until the user approves it; apps that ship with attached hardware are exempt.
/// </remarks>
public sealed class StoreAppUpdater : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly Func<IReadOnlyList<AppEntry>> _installed;
    private readonly Func<string, CancellationToken, Task<StoreCatalogVersion?>> _latest;
    private readonly Func<string, StoreCatalogVersion, CancellationToken, Task<StoreInstallRequest?>> _resolve;
    private readonly Func<StoreInstallRequest, CancellationToken, Task<StoreInstallResponse>> _install;
    private readonly Action _announce;
    private readonly StorePendingUpdates _pending;

    public StoreAppUpdater(
        AppRegistry registry,
        StoreCatalogProxy catalog,
        StoreEntitlements entitlements,
        StoreInstaller installer,
        HardwareAppCatalog hardware,
        StorePendingUpdates pending,
        MultiplexHub hub)
        : this(
            () => registry.All().ToList(),
            (appId, ct) => StoreRelease.LatestAsync(catalog, appId, NexusVersion(), ct),
            (appId, version, ct) => StoreRelease.ResolveAsync(entitlements, appId, version, NexusVersion(), hardware.IsMatched(appId), ct),
            installer.UpdateAsync,
            () => PanelTopics.BroadcastAppsChanged(hub),
            pending)
    {
    }

    /// <summary>Test seam: the registry snapshot, catalog read, entitlement resolve, install, announce and pending set.</summary>
    internal StoreAppUpdater(
        Func<IReadOnlyList<AppEntry>> installed,
        Func<string, CancellationToken, Task<StoreCatalogVersion?>> latest,
        Func<string, StoreCatalogVersion, CancellationToken, Task<StoreInstallRequest?>> resolve,
        Func<StoreInstallRequest, CancellationToken, Task<StoreInstallResponse>> install,
        Action announce,
        StorePendingUpdates pending)
    {
        _installed = installed;
        _latest = latest;
        _resolve = resolve;
        _install = install;
        _announce = announce;
        _pending = pending;
    }

    private static string NexusVersion() => BuildInfo.Version.TrimStart('v', 'V');

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keeps StartAsync off the startup critical path so /ping answers.
        await Task.Yield();
        try
        { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            { await TickAsync(stoppingToken).ConfigureAwait(false); }
            // An HttpClient timeout is an OperationCanceledException too; only shutdown ends the loop.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { ServiceLog.Error($"[store] app update check failed: {ex.GetType().Name}: {ex.Message}"); }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        { return false; }
    }

    /// <summary>Returns the ids updated this pass.</summary>
    internal async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var updated = new List<string>();
        var changed = false;
        // A snapshot: each install refreshes the registry under the loop.
        foreach (var entry in _installed())
        {
            // Focus mode holds the network; the next pass picks up what this one left.
            if (FocusNetworkGate.IsHeld) break;
            if (entry.Source != AppInstallPaths.Source.User) continue;
            var latest = await _latest(entry.Id, ct).ConfigureAwait(false);
            if (latest is null || !VersionCompare.IsNewer(latest.Version, entry.Manifest.Version))
            {
                changed |= _pending.Remove(entry.Id);
                continue;
            }

            var request = await _resolve(entry.Id, latest, ct).ConfigureAwait(false);
            if (request is null)
            {
                ServiceLog.Info($"[store] {entry.Id} {latest.Version} is out but not installable here (no entitlement, or the store is unreachable); staying on {entry.Manifest.Version}");
                continue;
            }

            var approved = AppCapabilityGrants.From(entry.Manifest.Capabilities);
            request.ApprovedCapabilities = approved.ToList();
            // The catalog's copy skips a download the installer would refuse; the downloaded manifest stays authoritative.
            if (!request.ConsentExempt && latest.Capabilities is not null)
            {
                var published = AppCapabilityGrants.From(latest.Capabilities);
                if (AppCapabilityGrants.Missing(published, approved).Count > 0)
                {
                    Hold(entry, latest.Version, published.ToList(), approved);
                    changed = true;
                    continue;
                }
            }
            var result = await _install(request, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                if (result.Reason == "consent_required" && result.RequestedCapabilities is { } requested)
                {
                    Hold(entry, latest.Version, requested, approved);
                    changed = true;
                    continue;
                }
                ServiceLog.Warn($"[store] update of {entry.Id} to {latest.Version} failed: {result.Reason}");
                continue;
            }
            changed |= _pending.Remove(entry.Id);
            ServiceLog.Info($"[store] updated {entry.Id} {entry.Manifest.Version} -> {latest.Version}");
            updated.Add(entry.Id);
        }
        if (updated.Count > 0 || changed) _announce();
        return updated;
    }

    private void Hold(AppEntry entry, string version, List<string> requested, IEnumerable<string> approved)
    {
        var added = AppCapabilityGrants.Missing(requested, approved);
        _pending.Set(new StorePendingUpdate
        {
            AppId = entry.Id,
            FromVersion = entry.Manifest.Version,
            Version = version,
            RequestedCapabilities = requested,
            NewCapabilities = added,
        });
        ServiceLog.Info($"[store] {entry.Id} {version} waits for approval of: {string.Join(", ", added)}");
    }
}
