using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>
/// Converts one journalctl `--output=json` line into a DiagnosticIncident.
/// Pure JSON parsing (JsonDocument, no source-gen context needed since this
/// reads an externally shaped DOM rather than binding a fixed type), no
/// Process/OS dependency, so it builds and runs on any platform. Never
/// throws: malformed or unrecognized input yields null.
///
/// journalctl's json export represents every field (including numeric ones
/// like PRIORITY and __REALTIME_TIMESTAMP) as a JSON string, so every field
/// read here goes through GetStr first, never a typed JSON number getter.
/// </summary>
public static class LinuxJournalIncidentParser
{
    /// <summary>Kernel error not otherwise classified below.</summary>
    public const string SourceKernel = "kernel";
    /// <summary>Kernel OOM killer terminated a process.</summary>
    public const string SourceOomKill = "oomKill";
    /// <summary>Userspace segmentation fault reported by the kernel.</summary>
    public const string SourceSegfault = "segfault";
    /// <summary>A systemd service unit failed. disk/appCrash reuse the
    /// Windows DiagnosticEventCatalog source constants of the same meaning.</summary>
    public const string SourceUnitFailed = "unitFailed";

    private static readonly IReadOnlyDictionary<string, string> EmptyData = new Dictionary<string, string>();

    // "Out of memory: Killed process 1234 (name) total-vm:..." - mm/oom_kill.c's
    // pr_err format, stable since the message was introduced.
    private static readonly Regex OomKillRegex =
        new(@"Killed process (\d+) \(([^)]+)\)", RegexOptions.None, TimeSpan.FromSeconds(2));

    // "name[1234]: segfault at 0 ip 00007f... sp 00007f... error 4 in lib..." -
    // arch fault handlers' show_signal_msg format.
    private static readonly Regex SegfaultRegex =
        new(@"^(\S+)\[(\d+)\]:\s*segfault at (\S+)", RegexOptions.None, TimeSpan.FromSeconds(2));

    // systemd-coredump's own "Process 1234 (name) of user 1000 dumped core."
    // notice; used only as a fallback when COREDUMP_COMM is absent.
    private static readonly Regex CoredumpProcessRegex =
        new(@"Process (\d+) \(([^)]+)\)", RegexOptions.None, TimeSpan.FromSeconds(2));

    public static DiagnosticIncident? Parse(string jsonLine)
    {
        try
        {
            return ParseCore(jsonLine);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses journalctl's newline-delimited JSON objects (one per
    /// journal entry - `--output=json` is NOT a single wrapping array).</summary>
    public static List<DiagnosticIncident> ParseLines(string output)
    {
        var result = new List<DiagnosticIncident>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var incident = Parse(line);
            if (incident is not null)
            {
                result.Add(incident);
            }
        }
        return result;
    }

    /// <summary>Newest __REALTIME_TIMESTAMP across every line, independent of
    /// whether the line classifies into an incident - a caller advancing a
    /// poll watermark must not stall behind lines this parser discards.</summary>
    public static DateTime? GetNewestTimestamp(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        DateTime? newest = null;
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            if (TryGetTimestamp(line) is { } t && (newest is null || t > newest))
            {
                newest = t;
            }
        }
        return newest;
    }

    private static DateTime? TryGetTimestamp(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ExtractRealtimeUtc(doc.RootElement) : null;
        }
        catch
        {
            return null;
        }
    }

    // 1 microsecond = 10 ticks (.NET ticks are 100ns units).
    private static DateTime? ExtractRealtimeUtc(JsonElement root) =>
        GetLong(root, "__REALTIME_TIMESTAMP") is { } us ? DateTime.UnixEpoch.AddTicks(us * 10) : null;

    private static DiagnosticIncident? ParseCore(string jsonLine)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var realtimeUs = GetLong(root, "__REALTIME_TIMESTAMP");
        if (realtimeUs is null)
        {
            return null;
        }
        var timeUtc = DateTime.UnixEpoch.AddTicks(realtimeUs.Value * 10);

        var cursor = GetStr(root, "__CURSOR");
        var transport = GetStr(root, "_TRANSPORT") ?? "";
        var syslogId = GetStr(root, "SYSLOG_IDENTIFIER") ?? "";
        var priority = GetInt(root, "PRIORITY");
        var message = GetStr(root, "MESSAGE") ?? "";

        var classified = Classify(transport, syslogId, priority, message, root);
        if (classified is null)
        {
            return null;
        }

        return new DiagnosticIncident
        {
            Id = "journal/" + (cursor ?? realtimeUs.Value.ToString(CultureInfo.InvariantCulture)),
            TimeUtc = timeUtc,
            Source = classified.Source,
            Severity = classified.Severity,
            Title = classified.Title,
            Detail = classified.Detail,
            App = classified.App,
            Data = classified.Data,
        };
    }

    private sealed record Classified(
        string Source, string Severity, string Title, string Detail, DiagnosticAppInfo? App, IReadOnlyDictionary<string, string> Data);

    // Order matters: oomKill/segfault are message-pattern matches independent
    // of priority (the kernel logs segfaults at KERN_INFO, below the "err"
    // priority the general query is filtered to - see LinuxJournalIncidentParserTests),
    // so they're checked first against any transport=kernel line regardless
    // of what query produced it.
    private static Classified? Classify(string transport, string syslogId, int? priority, string message, JsonElement root)
    {
        if (transport == "kernel")
        {
            var oom = OomKillRegex.Match(message);
            if (oom.Success)
            {
                var data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pid"] = oom.Groups[1].Value,
                    ["process"] = oom.Groups[2].Value,
                };
                return new Classified(SourceOomKill, DiagnosticSeverity.Critical, "Out-of-memory killer terminated a process",
                    $"The kernel OOM killer terminated {oom.Groups[2].Value} (pid {oom.Groups[1].Value}) to reclaim memory.", null, data);
            }

            if (message.Contains("segfault at", StringComparison.Ordinal))
            {
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                var seg = SegfaultRegex.Match(message);
                if (seg.Success)
                {
                    data["process"] = seg.Groups[1].Value;
                    data["pid"] = seg.Groups[2].Value;
                    data["address"] = seg.Groups[3].Value;
                }
                return new Classified(SourceSegfault, DiagnosticSeverity.Warning, "Application segmentation fault", message, null, data);
            }
        }

        if (syslogId == "systemd-coredump")
        {
            // COREDUMP_EXE/COREDUMP_COMM are fields systemd-coredump attaches
            // to its own journal entry describing the crashed process (not
            // the reporter) - the same fields coredumpctl filters on.
            var exe = GetStr(root, "COREDUMP_EXE") ?? "";
            var name = GetStr(root, "COREDUMP_COMM");
            if (string.IsNullOrEmpty(name))
            {
                var m = CoredumpProcessRegex.Match(message);
                name = m.Success ? m.Groups[2].Value : "";
            }
            var app = new DiagnosticAppInfo { Name = name ?? "", Path = exe, ExceptionCode = "", FaultingModule = "", IsGame = false };
            return new Classified(DiagnosticEventCatalog.SourceAppCrash, DiagnosticSeverity.Warning, "Application crash", message, app, EmptyData);
        }

        if (priority is <= 3 && syslogId == "systemd")
        {
            // UNIT=/USER_UNIT= carry the unit PID 1 is reporting ABOUT;
            // _SYSTEMD_UNIT is journald's trusted field for the LOGGING
            // process's own cgroup, which for PID 1 itself is "init.scope" -
            // not a real unit name, so it must not count as one.
            var unit = GetStr(root, "UNIT") ?? GetStr(root, "USER_UNIT");
            if (string.IsNullOrEmpty(unit))
            {
                var trusted = GetStr(root, "_SYSTEMD_UNIT");
                unit = trusted == "init.scope" ? null : trusted;
            }

            var isUnitFailure = !string.IsNullOrEmpty(unit)
                || message.Contains("Failed with result", StringComparison.Ordinal)
                || message.Contains("Failed to start", StringComparison.Ordinal);
            if (isUnitFailure)
            {
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(unit))
                {
                    data["unit"] = unit;
                }
                return new Classified(SourceUnitFailed, DiagnosticSeverity.Warning, "Systemd unit failed", message, null, data);
            }
        }

        if (priority is <= 3 && transport == "kernel" && message.Contains("I/O error", StringComparison.Ordinal))
        {
            return new Classified(DiagnosticEventCatalog.SourceDisk, DiagnosticSeverity.Warning, "Kernel-reported disk I/O error", message, null, EmptyData);
        }

        if (priority is <= 3 && transport == "kernel")
        {
            var severity = priority <= 2 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;
            return new Classified(SourceKernel, severity, "Kernel error", message, null, EmptyData);
        }

        return null;
    }

    private static string? GetStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(JsonElement obj, string prop) =>
        GetStr(obj, prop) is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static int? GetInt(JsonElement obj, string prop) =>
        GetStr(obj, prop) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
