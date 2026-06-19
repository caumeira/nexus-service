using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Plugins;

/// <summary>
/// Runtime registry of plugin-contributed device providers - the single seam the
/// four compile-time device subsystems (cooling, sensors, device-id, DFU) read
/// at their composite boundary so a plugin can add a source without a rebuild.
///
/// Phase 0 builds the seam empty (first-party providers keep their static DI
/// registrations); the out-of-process broker (Phase 2) populates it. Reads are
/// lock-free copy-on-write snapshots - the cooling tick routes through
/// <see cref="FanSources"/> on the hot path and must never block on a writer.
///
/// Ownership is the security spine: a plugin's surface is its cryptographic
/// prefix <c>plugin:&lt;appId&gt;:</c>, and the HOST builds the ownership
/// predicate from that prefix. A plugin never supplies its own <c>Owns</c>
/// lambda (T11), so it can never claim another plugin's - or a first-party -
/// channel. Writes to cooling/lighting stay first-party: a plugin only
/// registers sources the host routes to; the host holds the write loop.
/// </summary>
public sealed class PluginProviderRegistry
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, RegisteredProvider> _byId =
        ImmutableDictionary<string, RegisteredProvider>.Empty;

    // Copy-on-write snapshots, rebuilt under the lock on every mutation and read
    // lock-free. ImmutableArray is a single-reference struct, so a field read
    // copies one array reference (atomic) - the hot read path never tears or waits.
    private ImmutableArray<CompositeFanControlProvider.FanSource> _fanSources =
        ImmutableArray<CompositeFanControlProvider.FanSource>.Empty;
    private ImmutableArray<ISensorSource> _sensorSources = ImmutableArray<ISensorSource>.Empty;
    private ImmutableArray<IDeviceHandler> _handlers = ImmutableArray<IDeviceHandler>.Empty;
    private ImmutableArray<IDfuFlashTarget> _dfuTargets = ImmutableArray<IDfuFlashTarget>.Empty;

    /// <summary>Plugin fan sources, prefix-scoped, for the cooling composite.</summary>
    public ImmutableArray<CompositeFanControlProvider.FanSource> FanSources => _fanSources;

    /// <summary>Plugin read-only sensor sources for the sensor composite.</summary>
    public ImmutableArray<ISensorSource> SensorSources => _sensorSources;

    /// <summary>Plugin device handlers for <c>DeviceManager</c> detection.</summary>
    public ImmutableArray<IDeviceHandler> Handlers => _handlers;

    /// <summary>Plugin DFU flash targets for <c>FirmwareFlasher</c>.</summary>
    public ImmutableArray<IDfuFlashTarget> DfuTargets => _dfuTargets;

    /// <summary>Register (or replace) a plugin's providers. Idempotent per pluginId.</summary>
    public void Add(RegisteredProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _byId = _byId.SetItem(provider.PluginId, provider);
            Rebuild();
        }
    }

    /// <summary>Drop a plugin's providers (e.g. on child process exit).</summary>
    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(pluginId)) return;
            _byId = _byId.Remove(pluginId);
            Rebuild();
        }
    }

    /// <summary>
    /// The single ownership chokepoint. <paramref name="id"/> belongs to
    /// <paramref name="pluginId"/> iff that plugin is registered AND the id
    /// carries its cryptographic prefix. Called by every adapter on every RPC;
    /// the host - not the plugin - decides ownership.
    /// </summary>
    public bool AssertOwns(string pluginId, string id)
    {
        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(id)) return false;
        // Atomic single-reference read; no lock needed on the hot path.
        return _byId.TryGetValue(pluginId, out var p)
            && id.StartsWith(p.Prefix, StringComparison.Ordinal);
    }

    /// <summary>Snapshot of registered plugin ids (diagnostics / admin).</summary>
    public ImmutableArray<string> PluginIds => _byId.Keys.ToImmutableArray();

    // Rebuild all read snapshots from the current map. Must hold _gate.
    private void Rebuild()
    {
        var fans = ImmutableArray.CreateBuilder<CompositeFanControlProvider.FanSource>();
        var sensors = ImmutableArray.CreateBuilder<ISensorSource>();
        var handlers = ImmutableArray.CreateBuilder<IDeviceHandler>();
        var dfu = ImmutableArray.CreateBuilder<IDfuFlashTarget>();

        foreach (var p in _byId.Values)
        {
            if (p.Fans is not null)
            {
                // Host-built ownership predicate, closed over the verified prefix.
                var prefix = p.Prefix;
                fans.Add(new CompositeFanControlProvider.FanSource(
                    id => !string.IsNullOrEmpty(id) && id.StartsWith(prefix, StringComparison.Ordinal),
                    p.Fans));
            }
            if (p.Sensors is not null) sensors.Add(p.Sensors);
            if (p.Handler is not null) handlers.Add(p.Handler);
            if (p.Dfu is not null) dfu.Add(p.Dfu);
        }

        _fanSources = fans.ToImmutable();
        _sensorSources = sensors.ToImmutable();
        _handlers = handlers.ToImmutable();
        _dfuTargets = dfu.ToImmutable();
    }
}

/// <summary>
/// One plugin's registered providers, keyed by <see cref="PluginId"/>. A plugin
/// contributes any subset; the host wires each into the matching composite.
/// </summary>
public sealed record RegisteredProvider(
    string PluginId,
    string Prefix,
    CapabilityGrant Grant,
    IFanControlProvider? Fans = null,
    ISensorSource? Sensors = null,
    IDeviceHandler? Handler = null,
    IDfuFlashTarget? Dfu = null);

/// <summary>
/// A read-only sensor source a plugin contributes. Plugin sensors are namespaced
/// <c>plugin:&lt;appId&gt;:...</c> and read-only for everyone - a plugin can
/// surface telemetry but never drive cooling from it.
/// </summary>
public interface ISensorSource
{
    /// <summary>The plugin's sensors, ids already prefixed by the host caller.</summary>
    IReadOnlyList<HardwareSensor> GetSensors();
}

/// <summary>
/// The capability grant backing a registered provider. In Phase 0/1 this is a
/// first-party trusted grant constructed in-code; in Phase 3 the identical shape
/// is read from the verified signed cert - the consumers never change, only the
/// source. Grants live here, never in a plugin's manifest.
/// </summary>
public sealed record CapabilityGrant(string AppId, ImmutableHashSet<string> Surfaces)
{
    /// <summary>Surface names a grant may carry.</summary>
    public static class Surface
    {
        public const string Cooling = "cooling";
        public const string Lighting = "lighting";
        public const string Sensor = "sensor";
        public const string Widget = "widget";
        public const string Page = "page";
        public const string Touch = "touch";
    }

    /// <summary>The full-ceiling grant for an in-tree first-party provider.</summary>
    public static CapabilityGrant FirstParty(string appId) => new(
        appId,
        ImmutableHashSet.Create(
            Surface.Cooling, Surface.Lighting, Surface.Sensor,
            Surface.Widget, Surface.Page, Surface.Touch));

    public bool HasSurface(string surface) => Surfaces.Contains(surface);
}
