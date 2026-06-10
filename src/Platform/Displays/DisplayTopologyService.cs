using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Merge layer for GET /displays/topology: raw OS facts from the platform
/// provider, stamped with Nexus panel state (Y70 auto-panel detection via
/// the DDC controller-name fragments, hosting support, monitor-panel
/// assignments). Also keeps a short-lived attached-id cache so record
/// serialization can stamp displayAttached without a helper RPC per request.
/// </summary>
public sealed class DisplayTopologyService
{
    /// <summary>Kiosk hosting is overlay-based and Windows-only for now.</summary>
    public static bool HostingSupportedOnHost => HostingSupportedOverrideForTests ?? OperatingSystem.IsWindows();

    /// <summary>Lets the promote/demote integration tests run on any host OS.</summary>
    internal static bool? HostingSupportedOverrideForTests;

    private const string HelperUnavailableHint =
        "Displays are enumerated in the desktop session; the Nexus helper is not connected yet.";
    private const int AttachedIdsMaxAgeMs = 5000;

    private readonly IDisplayTopologyProvider _provider;
    private readonly PanelDeviceRegistry _panelRegistry;
    private readonly object _cacheLock = new();
    private HashSet<string>? _attachedIds;
    private long _attachedIdsAtMs;
    private bool _attachedIdsValid;

    public DisplayTopologyService(IDisplayTopologyProvider provider, PanelDeviceRegistry panelRegistry)
    {
        _provider = provider;
        _panelRegistry = panelRegistry;
    }

    public DisplayTopologyResponse GetTopology()
    {
        var raw = _provider.Enumerate();
        CacheAttachedIds(raw);
        var response = new DisplayTopologyResponse
        {
            HostingSupported = HostingSupportedOnHost,
            PositionsAvailable = _provider.PositionsAvailable,
            Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hint = raw is null ? HelperUnavailableHint : "",
        };
        if (raw is null) return response;

        foreach (var info in raw)
        {
            var isY70 = IsY70Display(info.RawHardwareId);
            var assigned = _panelRegistry.FindByDisplayId(info.Id);
            response.Displays.Add(new DisplayTopologyEntryDto
            {
                Id = info.Id,
                Number = info.Number,
                Name = info.Name,
                Manufacturer = info.Manufacturer,
                Model = info.Model,
                Bounds = info.X is int x && info.Y is int y
                    ? new DisplayBoundsDto { X = x, Y = y, Width = info.Width, Height = info.Height }
                    : null,
                Resolution = new DisplaySizeDto { Width = info.ResolutionWidth, Height = info.ResolutionHeight },
                ScaleFactor = info.Scale,
                Dpi = info.Dpi,
                IsPrimary = info.IsPrimary,
                IsInternal = info.IsInternal,
                IsTouch = info.IsTouch,
                Orientation = info.Orientation,
                IsY70 = isY70,
                HostingSupported = HostingSupportedOnHost && !isY70,
                AssignedPanelDeviceId = assigned?.Id,
                AssignedPanelName = assigned?.DisplayName,
            });
        }
        return response;
    }

    /// <summary>One entry from the current topology, or null when absent/unknown.</summary>
    public DisplayTopologyEntryDto? FindDisplay(string displayId)
    {
        foreach (var entry in GetTopology().Displays)
        {
            if (string.Equals(entry.Id, displayId, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Ids of currently-attached displays for displayAttached stamping. Null
    /// = topology unknown (no helper). Served from a short-lived cache; a
    /// stale cache re-enumerates inline.
    /// </summary>
    public HashSet<string>? GetAttachedIds()
    {
        lock (_cacheLock)
        {
            var now = Environment.TickCount64;
            if (_attachedIdsValid && now - _attachedIdsAtMs <= AttachedIdsMaxAgeMs)
                return _attachedIds;
        }
        var raw = _provider.Enumerate();
        return CacheAttachedIds(raw);
    }

    private HashSet<string>? CacheAttachedIds(IReadOnlyList<RawDisplayInfo>? raw)
    {
        HashSet<string>? ids = null;
        if (raw is not null)
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in raw) ids.Add(info.Id);
        }
        lock (_cacheLock)
        {
            _attachedIds = ids;
            _attachedIdsAtMs = Environment.TickCount64;
            _attachedIdsValid = true;
        }
        return ids;
    }

    internal static bool IsY70Display(string rawHardwareId)
    {
        if (string.IsNullOrEmpty(rawHardwareId)) return false;
        foreach (var fragment in Y70DisplayProtocol.DdcPanelHardwareNames)
        {
            if (rawHardwareId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
