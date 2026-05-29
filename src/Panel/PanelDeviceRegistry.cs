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
            if (patch.WidgetOpacity.HasValue)
                record.WidgetOpacity = patch.WidgetOpacity.Value;
            if (patch.WidgetLabels.HasValue)
                record.WidgetLabels = patch.WidgetLabels.Value;
            if (patch.ThemeSyncWithDesktop.HasValue)
                record.ThemeSyncWithDesktop = patch.ThemeSyncWithDesktop.Value;
            if (patch.AccentSyncWithDesktop.HasValue)
                record.AccentSyncWithDesktop = patch.AccentSyncWithDesktop.Value;
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
            WidgetOpacity = r.WidgetOpacity,
            WidgetLabels = r.WidgetLabels,
            ThemeSyncWithDesktop = r.ThemeSyncWithDesktop,
            AccentSyncWithDesktop = r.AccentSyncWithDesktop,
            FirstSeenAt = r.FirstSeenAt,
            LastSeenAt = r.LastSeenAt,
            Capabilities = r.Capabilities,
        };
    }
}
