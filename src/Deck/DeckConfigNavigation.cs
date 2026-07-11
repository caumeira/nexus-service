using System.Collections.Generic;

namespace Nexus.Service.Deck;

/// <summary>
/// Walks a <see cref="DeckConfig"/> tree by page index + folder path /
/// dot-joined slot path, mirroring nexus-web's <c>deckLayout.ts</c>
/// resolution rules. Shared by the connection worker (physical key -> slot)
/// and the routes (test-press, image slot addressing). The dot-joined slot
/// path itself stays page-independent (matches nexus-web's
/// computeViewUploadJobs, which does not prefix slotPath by page either) -
/// only the page selects which page's slot tree the path walks.
/// </summary>
public static class DeckConfigNavigation
{
    /// <summary>
    /// The slot list at the given page + folder path, or null when the page
    /// is out of range or the folder path no longer resolves (a folder was
    /// removed or replaced by a leaf action).
    /// </summary>
    public static List<DeckSlot>? ResolveView(DeckConfig config, int page, IReadOnlyList<int> folderPath)
    {
        if (page < 0 || page >= config.Pages.Count)
        {
            return null;
        }
        var slots = config.Pages[page].Slots;
        foreach (var idx in folderPath)
        {
            if (idx < 0 || idx >= slots.Count)
            {
                return null;
            }
            var folder = slots[idx].Folder;
            if (folder is null)
            {
                return null;
            }
            slots = folder.Slots;
        }
        return slots;
    }

    /// <summary>The slot at a dot-joined index path from a page's root, or null if it does not resolve.</summary>
    public static DeckSlot? ResolveSlot(DeckConfig config, int page, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            return null;
        }
        var parentPath = new List<int>(indices.Count - 1);
        for (var i = 0; i < indices.Count - 1; i++)
        {
            parentPath.Add(indices[i]);
        }
        var view = ResolveView(config, page, parentPath);
        if (view is null)
        {
            return null;
        }
        var last = indices[^1];
        return last >= 0 && last < view.Count ? view[last] : null;
    }

    /// <summary>Parses a route's dot-joined slotPath ("3" or "2.5") into indices, or null if malformed.</summary>
    public static List<int>? ParseSlotPath(string slotPath)
    {
        var parts = slotPath.Split('.');
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var idx) || idx < 0)
            {
                return null;
            }
            result.Add(idx);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Builds the dot-joined slotPath for a slot at folderPath + slotIndex.</summary>
    public static string BuildSlotPath(IReadOnlyList<int> folderPath, int slotIndex)
    {
        if (folderPath.Count == 0)
        {
            return slotIndex.ToString();
        }
        var sb = new System.Text.StringBuilder();
        foreach (var idx in folderPath)
        {
            sb.Append(idx).Append('.');
        }
        sb.Append(slotIndex);
        return sb.ToString();
    }
}
