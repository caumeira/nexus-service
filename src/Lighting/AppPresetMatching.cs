using System;
using System.Text;

namespace Nexus.Service.Lighting;

/// <summary>
/// Matches the focused window's process name against a preset's app bindings.
/// The two sides come from different sources - the focus signal carries a
/// process name ("chrome"), a Start-menu pick carries a display name ("Google
/// Chrome") - so a binding stores a process name resolved at bind time, and
/// display names are compared as well: resolution can be absent (UWP entries,
/// Linux .desktop files) or present but wrong (a launcher stub).
/// </summary>
public static class AppPresetMatching
{
    /// <summary>Shortest focused name accepted by the display-name fallback.
    /// Below this a substring hit is far more likely to be coincidence than a
    /// real match.</summary>
    private const int MinFallbackLength = 3;

    /// <summary>Match key for a process name: lower-cased, without a trailing
    /// ".exe". Both sides of an exact comparison run through this.</summary>
    public static string ProcessKey(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^4];
        }
        return s.ToLowerInvariant();
    }

    /// <summary>Match key for a display name: letters and digits only, so
    /// "Google Chrome" and "google-chrome" collapse to one form.</summary>
    public static string DisplayKey(string raw)
    {
        var sb = new StringBuilder((raw ?? "").Length);
        foreach (var c in raw ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>True when a binding of (bindingProcessName, bindingDisplayName)
    /// should activate for a window whose process name is focusedProcess.</summary>
    public static bool Matches(string bindingProcessName, string bindingDisplayName, string focusedProcess)
    {
        var focusedKey = ProcessKey(focusedProcess);
        if (focusedKey.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(bindingProcessName) && ProcessKey(bindingProcessName) == focusedKey)
        {
            return true;
        }

        // The display fallback runs even when a process name resolved, because
        // resolution can succeed and still be wrong: a Squirrel-packaged app
        // (Discord, Slack, Teams) has a Start-Menu shortcut targeting
        // Update.exe, so the stored name is "update" while the window belongs
        // to "Discord".
        // The process name usually appears inside the display name ("chrome" in
        // "Google Chrome"), never the other way round.
        var focusedDisplay = DisplayKey(focusedKey);
        var bindingDisplay = DisplayKey(bindingDisplayName);
        if (focusedDisplay.Length == 0 || bindingDisplay.Length == 0)
        {
            return false;
        }
        if (focusedDisplay == bindingDisplay)
        {
            return true;
        }
        return focusedDisplay.Length >= MinFallbackLength && bindingDisplay.Contains(focusedDisplay, StringComparison.Ordinal);
    }
}
