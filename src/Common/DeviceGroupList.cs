using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Common;

/// <summary>
/// Shared sanitizer for the lighting and cooling pages' user-made groups. The
/// client owns order and membership and PUTs the whole list, so this is the one
/// place the invariants hold: at most <see cref="MaxGroups"/> groups, a member in
/// at most one of them, and no blank ids or names.
/// </summary>
public static class DeviceGroupList
{
    public const int MaxGroups = 10;

    /// <summary>Name length cap, matching the client's rename field.</summary>
    public const int MaxNameLength = 20;

    /// <summary>
    /// The list as it will be stored. Groups past the cap are dropped and a
    /// member id repeated across groups stays in the first group that claims it,
    /// so a card can never render twice. An empty group is kept: one is created
    /// empty and stays that way until the user drags a card into it.
    /// </summary>
    public static List<DeviceGroup> Sanitize(IReadOnlyList<DeviceGroup>? incoming)
    {
        var result = new List<DeviceGroup>();
        if (incoming is null) return result;

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in incoming)
        {
            if (result.Count >= MaxGroups) break;

            var id = (group.Id ?? "").Trim();
            if (id.Length == 0 || !ids.Add(id)) continue;

            var members = new List<string>(group.Members.Count);
            foreach (var member in group.Members)
            {
                var trimmed = (member ?? "").Trim();
                if (trimmed.Length == 0) continue;
                if (!claimed.Add(trimmed)) continue;
                members.Add(trimmed);
            }
            var name = (group.Name ?? "").Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];

            result.Add(new DeviceGroup { Id = id, Name = name, Members = members, After = (group.After ?? "").Trim() });
        }
        return result;
    }
}
