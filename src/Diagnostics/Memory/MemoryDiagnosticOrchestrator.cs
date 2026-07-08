using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Diagnostics.Memory;

/// <summary>Result of the most recent Windows Memory Diagnostic run, read back
/// from the System event log. Result is one of passed/failed/unknown.</summary>
public sealed record MemoryTestResult(DateTime TimeUtc, string Result, string? Detail)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>
/// Orchestrates the built-in Windows Memory Diagnostic tool via bcdedit (schedule
/// a {memdiag} boot entry for next boot only) and reads back its last result from
/// the System event log (Microsoft-Windows-MemoryDiagnostics-Results 1201
/// completed / 1202 errors). Windows-only; self-gates on
/// <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
public sealed class MemoryDiagnosticOrchestrator
{
    private const int ShellTimeoutMs = 10_000;
    private const string MemdiagToken = "{memdiag}";

    // Boot-scheduled state and the last diagnostic run's result only change
    // across a reboot or an explicit Schedule()/Cancel() call, so both reads
    // are cached to keep GET /diagnostics/memory and the health aggregator
    // from spawning bcdedit/wevtutil on every poll.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();

    // Fallback when bcdedit's enum output can't be parsed - reflects the last
    // Schedule()/Cancel() call this process made, not persisted across restarts.
    private volatile bool _lastKnownScheduled;
    private bool _scheduledCacheValid;
    private DateTime _scheduledCachedAtUtc = DateTime.MinValue;

    private MemoryTestResult? _lastResultCached;
    private bool _lastResultCacheValid;
    private DateTime _lastResultCachedAtUtc = DateTime.MinValue;

    public bool Schedule()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var result = DiagnosticsShell.Run("bcdedit.exe", ShellTimeoutMs, "/bootsequence", MemdiagToken);
        var ok = result.ExitCode == 0;
        if (ok) _lastKnownScheduled = true;
        InvalidateScheduledCache();
        return ok;
    }

    public bool Cancel()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var result = DiagnosticsShell.Run("bcdedit.exe", ShellTimeoutMs, "/bootsequence", MemdiagToken, "/remove");
        var ok = result.ExitCode == 0;
        if (ok) _lastKnownScheduled = false;
        InvalidateScheduledCache();
        return ok;
    }

    public bool IsScheduled()
    {
        if (!OperatingSystem.IsWindows()) return false;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_scheduledCacheValid && now - _scheduledCachedAtUtc < CacheTtl)
            {
                return _lastKnownScheduled;
            }

            var result = DiagnosticsShell.Run("bcdedit.exe", ShellTimeoutMs, "/enum", "{bootmgr}");
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout))
            {
                _lastKnownScheduled = ParseBootSequenceHasMemdiag(result.Stdout);
            }
            _scheduledCacheValid = true;
            _scheduledCachedAtUtc = now;
            return _lastKnownScheduled;
        }
    }

    private void InvalidateScheduledCache()
    {
        lock (_gate)
        {
            _scheduledCacheValid = false;
        }
    }

    /// <summary>Pure parse: does a "bootsequence" line in `bcdedit /enum` output
    /// reference {memdiag}? Case-insensitive, tolerant of bcdedit's column
    /// alignment (spaces between the field name and value).</summary>
    internal static bool ParseBootSequenceHasMemdiag(string bcdeditEnumOutput)
    {
        foreach (var rawLine in bcdeditEnumOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("bootsequence", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(MemdiagToken, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public MemoryTestResult? LastResult()
    {
        if (!OperatingSystem.IsWindows()) return null;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastResultCacheValid && now - _lastResultCachedAtUtc < CacheTtl)
            {
                return _lastResultCached;
            }

            var result = DiagnosticsShell.Run("wevtutil.exe", ShellTimeoutMs,
                "qe", "System",
                "/q:*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]",
                "/rd:true", "/c:5", "/f:xml");
            if (result.ExitCode != 0)
            {
                return _lastResultCacheValid ? _lastResultCached : null;
            }

            _lastResultCached = ParseLastResultXml(result.Stdout);
            _lastResultCacheValid = true;
            _lastResultCachedAtUtc = now;
            return _lastResultCached;
        }
    }

    /// <summary>Pure parse of wevtutil's XML event dump (multiple sibling
    /// &lt;Event&gt; elements, no enclosing root - wrapped here before parsing).
    /// Newest 1201 anchors the last run; a 1202 within 60s of it means the run
    /// found errors. Returns null when no 1201/1202 event is present.</summary>
    internal static MemoryTestResult? ParseLastResultXml(string wevtutilXmlOutput)
    {
        if (string.IsNullOrWhiteSpace(wevtutilXmlOutput)) return null;

        XDocument doc;
        try { doc = XDocument.Parse($"<Events>{wevtutilXmlOutput}</Events>"); }
        catch { return null; }

        var events = new List<(int Id, DateTime TimeUtc, Dictionary<string, string> Data)>();
        foreach (var evt in doc.Descendants().Where(e => e.Name.LocalName == "Event"))
        {
            var ns = evt.Name.Namespace;
            var system = evt.Element(ns + "System");
            if (system is null) continue;

            var idText = system.Element(ns + "EventID")?.Value;
            if (!int.TryParse(idText, out var id) || (id != 1201 && id != 1202)) continue;

            var timeAttr = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTime.TryParse(timeAttr, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var timeUtc))
            {
                continue;
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            var eventData = evt.Element(ns + "EventData");
            if (eventData is not null)
            {
                foreach (var d in eventData.Elements(ns + "Data"))
                {
                    var name = d.Attribute("Name")?.Value ?? "";
                    if (name.Length > 0) data[name] = d.Value;
                }
            }

            events.Add((id, timeUtc, data));
        }

        if (events.Count == 0) return null;

        var newest = events.OrderByDescending(e => e.TimeUtc).First();
        if (newest.Id == 1202)
        {
            return new MemoryTestResult(newest.TimeUtc, MemoryTestResult.Failed, BuildDetail(newest.Data));
        }

        // newest.Id == 1201 (completed): a paired 1202 within a minute of it means
        // the same run reported errors.
        var nearbyError = events.FirstOrDefault(e =>
            e.Id == 1202 && Math.Abs((e.TimeUtc - newest.TimeUtc).TotalSeconds) <= 60);
        if (nearbyError.Data is not null)
        {
            return new MemoryTestResult(newest.TimeUtc, MemoryTestResult.Failed, BuildDetail(nearbyError.Data));
        }

        return new MemoryTestResult(newest.TimeUtc, MemoryTestResult.Passed, BuildDetail(newest.Data));
    }

    private static string? BuildDetail(Dictionary<string, string> data) =>
        data.Count == 0 ? null : string.Join("; ", data.Select(kv => $"{kv.Key}={kv.Value}"));
}
