using System;
using System.Linq;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.EventLog;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.EventLog;

/// <summary>Covers EventLogMonitor's Linux journalctl polling (PollJournalOnce)
/// via the injected journalctl seam - no real journalctl needed, and this runs
/// on every platform since the polling code itself has no #if LINUX gate.</summary>
public class EventLogMonitorLinuxPollingTests
{
    // Snapshot(30) filters to the last 30 days relative to DateTime.UtcNow at
    // call time, so fixture timestamps must be computed at test-run time
    // rather than hardcoded, or they age out of the window.
    private static readonly DateTimeOffset EventTime = DateTimeOffset.UtcNow.AddMinutes(-30);
    private static readonly DateTimeOffset Since = EventTime.AddHours(-1);
    private static long EventTimeUs => EventTime.ToUnixTimeMilliseconds() * 1000;

    private static string OomKillLine =>
        $$"""
        {"__CURSOR":"c1","__REALTIME_TIMESTAMP":"{{EventTimeUs}}","_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"Out of memory: Killed process 1 (a) total-vm:1kB"}
        """;

    private static string UnitFailedLine =>
        $$"""
        {"__CURSOR":"c2","__REALTIME_TIMESTAMP":"{{EventTimeUs}}","_TRANSPORT":"journal","SYSLOG_IDENTIFIER":"systemd","PRIORITY":"3","_SYSTEMD_UNIT":"a.service","MESSAGE":"a.service: Failed."}
        """;

    // An err-priority line the classifier discards (no unit, no known
    // phrase, not kernel transport) - exercises the raw-timestamp watermark
    // independent of classification.
    private static string UnclassifiableLine =>
        $$"""
        {"__CURSOR":"c3","__REALTIME_TIMESTAMP":"{{EventTimeUs}}","_TRANSPORT":"syslog","SYSLOG_IDENTIFIER":"some-daemon","PRIORITY":"3","MESSAGE":"something odd happened"}
        """;

    private static ShellResult Ok(string stdout) => new(0, stdout, "");
    private static ShellResult SpawnFailed() => new(-1, "", "");

    [Fact]
    public void PollJournalOnce_since_argument_uses_the_unambiguous_epoch_form()
    {
        // --since parses a plain clock string in the box's local timezone
        // regardless of any output-formatting flag - only the "@seconds"
        // epoch form is safe to construct from a UTC DateTimeOffset.
        string[]? capturedArgs = null;
        var monitor = new EventLogMonitor(args =>
        {
            capturedArgs = args;
            return Ok("");
        });

        monitor.PollJournalOnce(Since);

        var sinceIndex = Array.IndexOf(capturedArgs!, "--since");
        Assert.True(sinceIndex >= 0);
        var sinceArg = capturedArgs![sinceIndex + 1];
        Assert.StartsWith("@", sinceArg);
        Assert.Equal(Since.ToUnixTimeSeconds(), long.Parse(sinceArg.TrimStart('@')));
    }

    [Fact]
    public void PollJournalOnce_spawn_failure_on_both_queries_leaves_watermark_unchanged_and_unsupported()
    {
        var monitor = new EventLogMonitor(_ => SpawnFailed());

        var newWatermark = monitor.PollJournalOnce(Since);

        Assert.Equal(Since, newWatermark);
        Assert.False(monitor.IsLinuxSupported);
        Assert.Empty(monitor.Snapshot(30));
    }

    [Fact]
    public void PollJournalOnce_a_healthy_box_with_zero_matching_entries_still_reports_supported()
    {
        // Exit 0 + empty stdout (journalctl ran, found nothing at "err") must
        // not be indistinguishable from "journalctl is missing".
        var monitor = new EventLogMonitor(_ => Ok(""));

        monitor.PollJournalOnce(Since);

        Assert.True(monitor.IsLinuxSupported);
    }

    [Fact]
    public void PollJournalOnce_general_query_result_is_added_and_marks_supported()
    {
        var monitor = new EventLogMonitor(args => args.Contains("--grep") ? Ok("") : Ok(UnitFailedLine));

        var newWatermark = monitor.PollJournalOnce(Since);

        Assert.True(monitor.IsLinuxSupported);
        var incident = Assert.Single(monitor.Snapshot(30));
        Assert.Equal(LinuxJournalIncidentParser.SourceUnitFailed, incident.Source);
        Assert.True(newWatermark > Since);
    }

    [Fact]
    public void PollJournalOnce_same_entry_from_both_queries_is_added_only_once()
    {
        // An err-priority OOM kill would legitimately appear in BOTH the
        // general --priority=err query and the kernel-pattern --grep query.
        var monitor = new EventLogMonitor(_ => Ok(OomKillLine));

        monitor.PollJournalOnce(Since);

        var incident = Assert.Single(monitor.Snapshot(30));
        Assert.Equal(LinuxJournalIncidentParser.SourceOomKill, incident.Source);
    }

    [Fact]
    public void PollJournalOnce_second_call_with_advanced_watermark_does_not_readd_boundary_entry()
    {
        var monitor = new EventLogMonitor(args => args.Contains("--grep") ? Ok("") : Ok(OomKillLine));

        var watermark1 = monitor.PollJournalOnce(Since);
        // Simulate the next poll cycle starting exactly at the newest seen
        // timestamp (journalctl's --since is inclusive at that boundary).
        var watermark2 = monitor.PollJournalOnce(watermark1);

        Assert.Single(monitor.Snapshot(30));
        Assert.Equal(watermark1, watermark2);
    }

    [Fact]
    public void PollJournalOnce_advances_watermark_past_unclassifiable_lines()
    {
        // A window with only lines the classifier discards must still
        // advance past them, or the next poll re-fetches the same window
        // forever and can never see anything newer.
        var monitor = new EventLogMonitor(args => args.Contains("--grep") ? Ok("") : Ok(UnclassifiableLine));

        var newWatermark = monitor.PollJournalOnce(Since);

        Assert.Empty(monitor.Snapshot(30));
        Assert.True(newWatermark > Since);
    }

    [Fact]
    public void PollJournalOnce_retains_every_classifiable_incident_in_a_single_poll_and_watermark_matches_the_newest()
    {
        // Regression guard for a per-query take-N cap: journalctl returns
        // oldest-first, so capping the parsed list drops the newest
        // incidents while a watermark scanned from the full raw response
        // still advances past them - permanent loss.
        const int count = 5;
        var lines = Enumerable.Range(0, count).Select(i =>
            $$"""
            {"__CURSOR":"oom{{i}}","__REALTIME_TIMESTAMP":"{{EventTimeUs + i * 1_000_000}}","_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"Out of memory: Killed process {{i}} (proc{{i}}) total-vm:1kB"}
            """);
        var output = string.Join("\n", lines);
        var monitor = new EventLogMonitor(args => args.Contains("--grep") ? Ok("") : Ok(output));

        var newWatermark = monitor.PollJournalOnce(Since);

        Assert.Equal(count, monitor.Snapshot(30).Count);
        var expectedNewest = DateTime.UnixEpoch.AddTicks((EventTimeUs + (count - 1) * 1_000_000) * 10);
        Assert.Equal(expectedNewest, newWatermark.UtcDateTime);
    }

    [Fact]
    public void PollJournalOnce_supported_flag_latches_true_even_if_a_later_poll_returns_nothing()
    {
        var callCount = 0;
        var monitor = new EventLogMonitor(args =>
        {
            callCount++;
            return callCount <= 2 ? Ok(UnitFailedLine) : Ok("");
        });

        monitor.PollJournalOnce(Since);
        Assert.True(monitor.IsLinuxSupported);

        monitor.PollJournalOnce(DateTimeOffset.UtcNow);
        Assert.True(monitor.IsLinuxSupported);
    }
}
