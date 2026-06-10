using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// Owns the per-device panel records persisted under
/// <c>NexusSettings.PanelDevices</c> (top-level, NOT profile-scoped).
/// Allocation, lookup, patching, removal all flow through here. Phone
/// pairing keeps its own auth-token registry; this is the identity +
/// layout surface that any panel device (Y70, kiosk, phone) maps onto via
/// the URL <c>/panel/{deviceId}</c>. Storage is global because device
/// identity describes physical hardware, not user preferences - profile
/// switches preserve the registry so the connected panel never loses its
/// own record mid-session.
/// </summary>
public sealed class PanelDeviceRegistry
{
    private readonly IConfigStore _store;

    public PanelDeviceRegistry(IConfigStore store)
    {
        _store = store;
    }

    public PanelDeviceRecord Allocate(string? displayName, PanelDeviceCapabilities? capabilities)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new PanelDeviceRecord
        {
            Id = NewId(),
            DisplayName = NormalizeName(displayName) ?? DefaultName(now),
            FirstSeenAt = now,
            LastSeenAt = now,
            Capabilities = capabilities,
        };

        _store.Update(s =>
        {
            s.PanelDevices[record.Id] = record;
        });

        return record;
    }

    /// <summary>
    /// Allocate a record bound to an OS monitor (promoted via
    /// POST /displays/{id}/panel). The binding makes the record the layout +
    /// kiosk identity for that display until demoted. Uniqueness on
    /// <paramref name="displayId"/> is enforced inside the store transaction:
    /// the route's pre-check races with concurrent promotes (double-click,
    /// two dashboards), and a duplicate binding would host two kiosks on one
    /// monitor and orphan a record on demote. Returns
    /// <c>Created == false</c> with the existing record when already bound.
    /// </summary>
    public (PanelDeviceRecord Record, bool Activated) AllocateForDisplay(string displayId, string? displayName, PanelDeviceCapabilities? capabilities)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new PanelDeviceRecord
        {
            Id = NewId(),
            DisplayName = NormalizeName(displayName) ?? DefaultName(now),
            FirstSeenAt = now,
            LastSeenAt = now,
            Capabilities = capabilities,
            DisplayId = displayId,
        };

        PanelDeviceRecord? result = null;
        var activated = false;
        _store.Update(s =>
        {
            foreach (var candidate in s.PanelDevices.Values)
            {
                if (!string.Equals(candidate.DisplayId, displayId, StringComparison.Ordinal)) continue;
                if (candidate.Enabled == false)
                {
                    // Turning the panel back on: same record, so layout,
                    // theme, name, and reserve persist through off/on
                    // cycles. Viewport hints refresh from the current OS
                    // facts (resolution/scale/touch may have changed).
                    candidate.Enabled = true;
                    candidate.Capabilities = capabilities ?? candidate.Capabilities;
                    candidate.LastSeenAt = now;
                    activated = true;
                }
                result = Clone(candidate);
                return;
            }
            s.PanelDevices[record.Id] = record;
        });

        // Panel on/off is rare and must survive an immediate service exit —
        // a write lost to the flush debounce would silently undo the toggle.
        _store.FlushNow();
        if (result is not null) return (result, activated);
        return (record, true);
    }

    /// <summary>
    /// Turn a display's panel OFF without losing its configuration: the
    /// record (layout/theme/settings) stays; assignments stop listing it so
    /// the overlay closes the kiosk. Returns the disabled record, or null
    /// when the display has no active monitor-panel record.
    /// </summary>
    public PanelDeviceRecord? DisablePanelForDisplay(string displayId)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return null;
        PanelDeviceRecord? snapshot = null;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                if (record.Enabled == false) return;
                record.Enabled = false;
                snapshot = Clone(record);
                return;
            }
        });
        // Same durability rule as AllocateForDisplay: the OFF must not be
        // lost to the debounce window if the service exits right after.
        _store.FlushNow();
        return snapshot;
    }

    /// <summary>
    /// Persist the last orientation applied through Nexus on the display's
    /// record (settings permanence, same model as the Y70's persisted
    /// orientation). The live OS rotation in /displays/topology stays the
    /// authoritative read; this survives service restarts and replug.
    /// </summary>
    public void UpdateDisplayOrientation(string displayId, string orientation)
    {
        if (string.IsNullOrWhiteSpace(displayId)) return;
        _store.Update(s =>
        {
            foreach (var record in s.PanelDevices.Values)
            {
                if (!string.Equals(record.DisplayId, displayId, StringComparison.Ordinal)) continue;
                record.Capabilities ??= new PanelDeviceCapabilities();
                record.Capabilities.Orientation = orientation;
                return;
            }
        });
    }

    public PanelDeviceRecord? FindByDisplayId(string displayId)
    {
        if (string.IsNullOrWhiteSpace(displayId))
            return null;
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (string.Equals(record.DisplayId, displayId, StringComparison.Ordinal))
                return Clone(record);
        }
        return null;
    }

    /// <summary>Active displayId -> panelDeviceId bindings (kiosk reconcile
    /// input). Disabled panels keep their record but host no kiosk.</summary>
    public IReadOnlyList<(string DisplayId, string PanelDeviceId, bool ReserveMonitor)> ListAssignments()
    {
        var assignments = new List<(string, string, bool)>();
        foreach (var record in _store.Load().PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && record.Enabled != false)
                assignments.Add((record.DisplayId, record.Id, record.ReserveMonitor ?? true));
        }
        return assignments;
    }

    public IReadOnlyList<PanelDeviceRecord> List()
    {
        var devices = _store.Load().PanelDevices;
        if (devices.Count == 0)
            return Array.Empty<PanelDeviceRecord>();
        return devices.Values
            .OrderByDescending(d => d.LastSeenAt)
            .Select(Clone)
            .ToList();
    }

    public PanelDeviceRecord? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        var devices = _store.Load().PanelDevices;
        if (!devices.TryGetValue(id, out var record))
            return null;
        return Clone(record);
    }

    public PanelDeviceRecord? Touch(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;
            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        return snapshot;
    }

    public PanelDeviceRecord? Patch(string id, PanelDevicePatch patch)
    {
        if (string.IsNullOrWhiteSpace(id) || patch is null)
            return null;

        PanelDeviceRecord? snapshot = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            if (!s.PanelDevices.TryGetValue(id, out var record))
                return;

            if (patch.DisplayName is not null)
            {
                var name = NormalizeName(patch.DisplayName);
                if (!string.IsNullOrEmpty(name))
                    record.DisplayName = name;
            }
            if (patch.Layout is not null)
                record.Layout = patch.Layout;
            if (patch.ThemeMode is not null)
                record.ThemeMode = NullIfEmpty(patch.ThemeMode);
            if (patch.AccentColor is not null)
                record.AccentColor = NullIfEmpty(patch.AccentColor);
            if (patch.BackgroundColor is not null)
                record.BackgroundColor = NullIfEmpty(patch.BackgroundColor);
            if (patch.BackgroundColorLight is not null)
                record.BackgroundColorLight = NullIfEmpty(patch.BackgroundColorLight);
            if (patch.BackgroundMode is not null)
                record.BackgroundMode = NullIfEmpty(patch.BackgroundMode);
            if (patch.BackgroundEffect is not null)
                record.BackgroundEffect = NullIfEmpty(patch.BackgroundEffect);
            if (patch.BackgroundTemplate.HasValue)
                record.BackgroundTemplate = patch.BackgroundTemplate.Value;
            if (patch.BackgroundOpacity.HasValue)
                record.BackgroundOpacity = patch.BackgroundOpacity.Value;
            // Web always sends a concrete state (reseeded on effect/template
            // change), so a non-null patch is authoritative; no clear-to-null.
            if (patch.BackgroundEffectState is not null)
                record.BackgroundEffectState = patch.BackgroundEffectState;
            if (patch.WidgetOpacity.HasValue)
                record.WidgetOpacity = patch.WidgetOpacity.Value;
            if (patch.WidgetLabels.HasValue)
                record.WidgetLabels = patch.WidgetLabels.Value;
            if (patch.WidgetBlur.HasValue)
                record.WidgetBlur = patch.WidgetBlur.Value;
            if (patch.ThemeSyncWithDesktop.HasValue)
                record.ThemeSyncWithDesktop = patch.ThemeSyncWithDesktop.Value;
            if (patch.AccentSyncWithDesktop.HasValue)
                record.AccentSyncWithDesktop = patch.AccentSyncWithDesktop.Value;
            // Reserve only makes sense for display-bound (promoted monitor)
            // records; the Y70 kiosk uses the global preference.
            if (patch.ReserveMonitor.HasValue && !string.IsNullOrEmpty(record.DisplayId))
                record.ReserveMonitor = patch.ReserveMonitor.Value;
            if (patch.Capabilities is not null)
                record.Capabilities = patch.Capabilities;

            record.LastSeenAt = now;
            snapshot = Clone(record);
        });
        return snapshot;
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var removed = false;
        _store.Update(s =>
        {
            removed = s.PanelDevices.Remove(id);
        });
        return removed;
    }

    private static string DefaultName(long now)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime;
        return $"Panel {when:MMM d HH:mm}";
    }

    private static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NewId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static PanelDeviceRecord Clone(PanelDeviceRecord r)
    {
        return new PanelDeviceRecord
        {
            Id = r.Id,
            DisplayName = r.DisplayName,
            Layout = r.Layout,
            ThemeMode = r.ThemeMode,
            AccentColor = r.AccentColor,
            BackgroundColor = r.BackgroundColor,
            BackgroundColorLight = r.BackgroundColorLight,
            BackgroundMode = r.BackgroundMode,
            BackgroundEffect = r.BackgroundEffect,
            BackgroundTemplate = r.BackgroundTemplate,
            BackgroundOpacity = r.BackgroundOpacity,
            BackgroundEffectState = r.BackgroundEffectState,
            WidgetOpacity = r.WidgetOpacity,
            WidgetLabels = r.WidgetLabels,
            WidgetBlur = r.WidgetBlur,
            ThemeSyncWithDesktop = r.ThemeSyncWithDesktop,
            AccentSyncWithDesktop = r.AccentSyncWithDesktop,
            FirstSeenAt = r.FirstSeenAt,
            LastSeenAt = r.LastSeenAt,
            Capabilities = r.Capabilities,
            DisplayId = r.DisplayId,
            ReserveMonitor = r.ReserveMonitor,
            Enabled = r.Enabled,
        };
    }
}
