using System;
using System.Collections.Generic;

namespace Nexus.Service.Helper;

/// <summary>
/// Field-diagnostic switches for the Windows helper's periodic pollers, so a
/// user reporting a desktop stall can bisect which poller causes it without a
/// build per hypothesis. Every poller runs by default; naming one in
/// NEXUS_HELPER_DISABLE makes its tick a no-op (the object is still
/// constructed, so the request handlers that depend on it keep working).
///
/// Also raises the slow-pass log line: a tick that exceeds
/// <see cref="SlowPassMs"/> reports its duration, which is the measurement
/// that says whether a poller is stalling at all.
/// </summary>
public static class HelperPollerDiagnostics
{
    public const string EnvVarName = "NEXUS_HELPER_DISABLE";

    /// <summary>Argv flag prefix UserHelperBootstrapper forwards in place of
    /// EnvVarName - a machine environment variable change on the LocalSystem
    /// service side does not reach a process schtasks spawns in another
    /// session, but an argv value does. Same hop WindowDiagnostics takes.</summary>
    public const string HelperArgPrefix = "--helper-disable=";

    public const string WindowSet = "windowset";
    public const string ScreenTime = "screentime";
    public const string Audio = "audio";
    public const string Watchdog = "watchdog";

    public static readonly string[] Known = { WindowSet, ScreenTime, Audio, Watchdog };

    /// <summary>A tick slower than this is worth a log line; a healthy pass is single-digit ms.</summary>
    public const int SlowPassMs = 25;

    private static HashSet<string> s_disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Comma- or space-separated poller names, case-insensitive. An unrecognized
    /// name is kept rather than dropped so a typo shows up verbatim in the
    /// startup log line instead of reading as "nothing was disabled". Tokens
    /// carrying anything outside [A-Za-z0-9_-] are dropped entirely: the parsed
    /// list is re-emitted into the scheduled-task XML UserHelperBootstrapper
    /// writes as LocalSystem, so it must not carry markup.
    /// </summary>
    public static HashSet<string> Parse(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;
        foreach (var part in value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0 && IsSafeToken(name)) set.Add(name);
        }
        return set;
    }

    private static bool IsSafeToken(string token)
    {
        foreach (var c in token)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Called once from WindowsUserHelper.Run, before any poller is constructed.</summary>
    public static void Configure(string? value) => s_disabled = Parse(value);

    public static bool IsDisabled(string poller) => s_disabled.Contains(poller);

    /// <summary>
    /// One line at helper start so a collected log is unambiguous about what was
    /// off. Names that match no poller are called out separately - a silent
    /// typo would otherwise read as a poller that was ruled out and was not.
    /// </summary>
    public static string FormatStartupLine(HashSet<string> disabled)
    {
        if (disabled.Count == 0) return "[helper-perf] all pollers enabled";

        var recognized = new List<string>();
        var unrecognized = new List<string>();
        foreach (var name in disabled)
        {
            (Array.Exists(Known, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                ? recognized
                : unrecognized).Add(name);
        }

        var line = recognized.Count > 0
            ? $"[helper-perf] disabled: {string.Join(",", recognized)}"
            : "[helper-perf] all pollers enabled";
        if (unrecognized.Count > 0)
        {
            line += $" (unrecognized, ignored: {string.Join(",", unrecognized)}; known: {string.Join(",", Known)})";
        }
        return line;
    }

    public static string FormatSlowPass(string poller, double elapsedMs, int items) =>
        $"[helper-perf] {poller} pass={elapsedMs:F1}ms items={items}";

    /// <summary>Exposed for the startup line; Configure already stored it.</summary>
    public static HashSet<string> Current => s_disabled;
}
