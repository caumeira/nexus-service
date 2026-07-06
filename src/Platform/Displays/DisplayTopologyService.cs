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
    /// <summary>Kiosk hosting: nexus-overlay on Windows, the overlay-helper
    /// sidecar on macOS, a user-session Chromium kiosk on Linux.</summary>
    public static bool HostingSupportedOnHost => HostingSupportedOverrideForTests
        ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    /// <summary>Promoted-monitor rotation goes through ChangeDisplaySettingsEx;
    /// macOS has no public rotation API and Linux layout is compositor-owned.</summary>
    public static bool RotationSupportedOnHost => OperatingSystem.IsWindows();

    /// <summary>"Keep panel clear of other windows" is the overlay's
    /// PanelMonitorGuard, Windows-only. macOS kiosks sit above app windows
    /// by level; Linux kiosks rely on the compositor.</summary>
    public static bool ReserveSupportedOnHost => OperatingSystem.IsWindows();

    /// <summary>Lets the promote/demote integration tests run on any host OS.</summary>
    internal static bool? HostingSupportedOverrideForTests;

    private const string HelperUnavailableHint =
        "Displays are enumerated in the desktop session; the Nexus helper is not connected yet.";
    private const int AttachedIdsMaxAgeMs = 5000;

    private readonly IDisplayTopologyProvider _provider;
    private readonly PanelDeviceRegistry _panelRegistry;
    private readonly object _cacheLock = new();
    private HashSet<string>? _attachedIds;
    private bool _hasY70Display;
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
            RotationSupported = RotationSupportedOnHost,
            ReserveSupported = ReserveSupportedOnHost,
            PositionsAvailable = _provider.PositionsAvailable,
            Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hint = raw is null ? HelperUnavailableHint : "",
        };
        if (raw is null) return response;

        foreach (var info in raw)
        {
            var isY70 = IsY70Display(info.RawHardwareId);
            // A disabled (turned-off) panel keeps its record but the display
            // reads as unassigned: the UI offers "Use as Nexus panel", which
            // re-activates the same record.
            var assigned = _panelRegistry.FindByDisplayId(info.Id);
            if (assigned?.Enabled == false) assigned = null;
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
        if (TryGetCached(out var ids, out _)) return ids;
        return CacheAttachedIds(_provider.Enumerate());
    }

    /// <summary>
    /// Whether any currently-attached display matches the Y70's DDC/EDID
    /// hardware-id fragments. Served from the same short-lived cache as
    /// <see cref="GetAttachedIds"/> so device-list polling never issues a
    /// helper RPC per call.
    /// </summary>
    public bool HasY70Display()
    {
        if (TryGetCached(out _, out var hasY70)) return hasY70;
        CacheAttachedIds(_provider.Enumerate());
        lock (_cacheLock) return _hasY70Display;
    }

    private bool TryGetCached(out HashSet<string>? ids, out bool hasY70)
    {
        lock (_cacheLock)
        {
            var now = Environment.TickCount64;
            if (_attachedIdsValid && now - _attachedIdsAtMs <= AttachedIdsMaxAgeMs)
            {
                ids = _attachedIds;
                hasY70 = _hasY70Display;
                return true;
            }
        }
        ids = null;
        hasY70 = false;
        return false;
    }

    private HashSet<string>? CacheAttachedIds(IReadOnlyList<RawDisplayInfo>? raw)
    {
        HashSet<string>? ids = null;
        var hasY70 = false;
        if (raw is not null)
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in raw)
            {
                ids.Add(info.Id);
                if (!hasY70 && IsY70Display(info.RawHardwareId)) hasY70 = true;
            }
        }
        lock (_cacheLock)
        {
            _attachedIds = ids;
            _hasY70Display = hasY70;
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
