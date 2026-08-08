using System;
using Nexus.Service.Diagnostics.EventLog;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.EventLog;

/// <summary>
/// Fixtures are synthetic journalctl `--output=json` lines, hand-authored
/// from documented journal export field names (__REALTIME_TIMESTAMP,
/// __CURSOR, _TRANSPORT, PRIORITY, SYSLOG_IDENTIFIER, MESSAGE - all strings,
/// per systemd's json export format) and well-known kernel/systemd message
/// text (mm/oom_kill.c's "Killed process" line, the fault handler's
/// "segfault at" line, systemd-coredump's "Process N (name) ... dumped
/// core." notice). Not captured from a real journal.
/// </summary>
public class LinuxJournalIncidentParserTests
{
    private const string OomKillLine = """
    {"__CURSOR":"s=a;i=1;b=x;m=1;t=1;x=1","__REALTIME_TIMESTAMP":"1723000000000000","_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"Out of memory: Killed process 1234 (chromium) total-vm:8000000kB, anon-rss:4000000kB, file-rss:0kB"}
    """;

    private const string SegfaultLine = """
    {"__CURSOR":"s=a;i=2;b=x;m=2;t=2;x=2","__REALTIME_TIMESTAMP":"1723000001000000","_TRANSPORT":"kernel","PRIORITY":"6","MESSAGE":"chromium[5678]: segfault at 0 ip 00007f1234567890 sp 00007ffe12345678 error 4 in libc.so.6"}
    """;

    private const string CoredumpLine = """
    {"__CURSOR":"s=a;i=3;b=x;m=3;t=3;x=3","__REALTIME_TIMESTAMP":"1723000002000000","_TRANSPORT":"journal","SYSLOG_IDENTIFIER":"systemd-coredump","PRIORITY":"2","MESSAGE":"Process 9999 (myapp) of user 1000 dumped core.","COREDUMP_EXE":"/usr/bin/myapp","COREDUMP_COMM":"myapp"}
    """;

    // A real PID 1 message about a unit failure: _SYSTEMD_UNIT is PID 1's own
    // cgroup (init.scope), UNIT carries the unit actually being reported on.
    private const string UnitFailedLine = """
    {"__CURSOR":"s=a;i=4;b=x;m=4;t=4;x=4","__REALTIME_TIMESTAMP":"1723000003000000","_TRANSPORT":"journal","SYSLOG_IDENTIFIER":"systemd","PRIORITY":"3","_PID":"1","_SYSTEMD_UNIT":"init.scope","UNIT":"nginx.service","MESSAGE":"nginx.service: Failed with result 'exit-code'."}
    """;

    private const string UnitFailedByPhraseNoUnitFieldLine = """
    {"__CURSOR":"s=a;i=4b;b=x;m=4;t=4;x=4","__REALTIME_TIMESTAMP":"1723000003500000","_TRANSPORT":"journal","SYSLOG_IDENTIFIER":"systemd","PRIORITY":"3","MESSAGE":"Failed to start nginx - high performance web server."}
    """;

    // PID 1 logging its own internal error, no UNIT field - _SYSTEMD_UNIT is
    // still init.scope here, which must not be read as a real unit name.
    private const string InitScopeOnlyNonUnitErrorLine = """
    {"__CURSOR":"s=a;i=4c;b=x;m=4;t=4;x=4","__REALTIME_TIMESTAMP":"1723000003700000","_TRANSPORT":"journal","SYSLOG_IDENTIFIER":"systemd","PRIORITY":"3","_PID":"1","_SYSTEMD_UNIT":"init.scope","MESSAGE":"Failed to enumerate cgroup v2 controllers, ignoring: No such file or directory"}
    """;

    private const string DiskIoErrorLine = """
    {"__CURSOR":"s=a;i=5;b=x;m=5;t=5;x=5","__REALTIME_TIMESTAMP":"1723000004000000","_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"blk_update_request: I/O error, dev sda, sector 12345"}
    """;

    private const string GenericKernelErrorLine = """
    {"__CURSOR":"s=a;i=6;b=x;m=6;t=6;x=6","__REALTIME_TIMESTAMP":"1723000005000000","_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"ata1.00: exception Emask 0x0 SAct 0x0 SErr 0x0 action 0x0"}
    """;

    private const string GenericKernelCriticalLine = """
    {"__CURSOR":"s=a;i=7;b=x;m=7;t=7;x=7","__REALTIME_TIMESTAMP":"1723000006000000","_TRANSPORT":"kernel","PRIORITY":"1","MESSAGE":"panic-ish critical kernel message"}
    """;

    private const string UnclassifiedInfoKernelLine = """
    {"__CURSOR":"s=a;i=8;b=x;m=8;t=8;x=8","__REALTIME_TIMESTAMP":"1723000007000000","_TRANSPORT":"kernel","PRIORITY":"6","MESSAGE":"eth0: link becomes ready"}
    """;

    private const string UnrelatedErrDaemonLine = """
    {"__CURSOR":"s=a;i=9;b=x;m=9;t=9;x=9","__REALTIME_TIMESTAMP":"1723000008000000","_TRANSPORT":"syslog","SYSLOG_IDENTIFIER":"some-random-daemon","PRIORITY":"3","MESSAGE":"something went wrong"}
    """;

    private const string MissingTimestampLine = """
    {"_TRANSPORT":"kernel","PRIORITY":"3","MESSAGE":"Out of memory: Killed process 1 (init)"}
    """;

    [Fact]
    public void OomKill_maps_source_severity_and_process_data()
    {
        var incident = LinuxJournalIncidentParser.Parse(OomKillLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceOomKill, incident!.Source);
        Assert.Equal(DiagnosticSeverity.Critical, incident.Severity);
        Assert.Equal("1234", incident.Data["pid"]);
        Assert.Equal("chromium", incident.Data["process"]);
        Assert.Equal("journal/s=a;i=1;b=x;m=1;t=1;x=1", incident.Id);
    }

    [Fact]
    public void Segfault_is_matched_even_at_info_priority()
    {
        // Priority 6 (info) is below the general query's --priority=err cutoff -
        // this proves the classifier doesn't gate segfault detection on priority.
        var incident = LinuxJournalIncidentParser.Parse(SegfaultLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceSegfault, incident!.Source);
        Assert.Equal(DiagnosticSeverity.Warning, incident.Severity);
        Assert.Equal("chromium", incident.Data["process"]);
        Assert.Equal("5678", incident.Data["pid"]);
    }

    [Fact]
    public void Systemd_coredump_maps_to_appCrash_with_app_info_from_coredump_fields()
    {
        var incident = LinuxJournalIncidentParser.Parse(CoredumpLine);

        Assert.NotNull(incident);
        Assert.Equal(DiagnosticEventCatalog.SourceAppCrash, incident!.Source);
        Assert.NotNull(incident.App);
        Assert.Equal("myapp", incident.App!.Name);
        Assert.Equal("/usr/bin/myapp", incident.App.Path);
        Assert.False(incident.App.IsGame);
    }

    [Fact]
    public void Systemd_unit_failure_maps_source_and_carries_unit_name()
    {
        var incident = LinuxJournalIncidentParser.Parse(UnitFailedLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceUnitFailed, incident!.Source);
        Assert.Equal("nginx.service", incident.Data["unit"]);
    }

    [Fact]
    public void Systemd_unit_failure_by_message_phrase_is_recognized_without_the_unit_field()
    {
        var incident = LinuxJournalIncidentParser.Parse(UnitFailedByPhraseNoUnitFieldLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceUnitFailed, incident!.Source);
        Assert.False(incident.Data.ContainsKey("unit"));
    }

    [Fact]
    public void Systemd_init_scope_only_error_at_err_priority_is_not_misclassified_as_unit_failed()
    {
        // _SYSTEMD_UNIT=init.scope (PID 1's own cgroup), no UNIT field, and
        // no unit-failure phrasing - must not read as a failed service.
        Assert.Null(LinuxJournalIncidentParser.Parse(InitScopeOnlyNonUnitErrorLine));
    }

    [Fact]
    public void Kernel_disk_io_error_maps_to_shared_disk_source()
    {
        var incident = LinuxJournalIncidentParser.Parse(DiskIoErrorLine);

        Assert.NotNull(incident);
        Assert.Equal(DiagnosticEventCatalog.SourceDisk, incident!.Source);
    }

    [Fact]
    public void Generic_kernel_error_at_priority_err_is_warning()
    {
        var incident = LinuxJournalIncidentParser.Parse(GenericKernelErrorLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceKernel, incident!.Source);
        Assert.Equal(DiagnosticSeverity.Warning, incident.Severity);
    }

    [Fact]
    public void Generic_kernel_error_at_priority_crit_is_critical()
    {
        var incident = LinuxJournalIncidentParser.Parse(GenericKernelCriticalLine);

        Assert.NotNull(incident);
        Assert.Equal(LinuxJournalIncidentParser.SourceKernel, incident!.Source);
        Assert.Equal(DiagnosticSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void Unclassified_info_level_kernel_line_is_discarded()
    {
        Assert.Null(LinuxJournalIncidentParser.Parse(UnclassifiedInfoKernelLine));
    }

    [Fact]
    public void Unrelated_non_kernel_non_systemd_source_is_discarded()
    {
        Assert.Null(LinuxJournalIncidentParser.Parse(UnrelatedErrDaemonLine));
    }

    [Fact]
    public void Missing_realtime_timestamp_is_discarded()
    {
        Assert.Null(LinuxJournalIncidentParser.Parse(MissingTimestampLine));
    }

    [Fact]
    public void Malformed_json_is_discarded_not_thrown()
    {
        Assert.Null(LinuxJournalIncidentParser.Parse("not json"));
        Assert.Null(LinuxJournalIncidentParser.Parse(""));
    }

    [Fact]
    public void Timestamp_converts_microseconds_since_epoch_to_utc()
    {
        var incident = LinuxJournalIncidentParser.Parse(OomKillLine);

        Assert.NotNull(incident);
        var expected = DateTime.UnixEpoch.AddTicks(1723000000000000L * 10);
        Assert.Equal(expected, incident!.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, incident.TimeUtc.Kind);
    }

    [Fact]
    public void ParseLines_splits_newline_delimited_entries_and_skips_blank_lines()
    {
        var output = string.Join("\n", OomKillLine, "", SegfaultLine, UnitFailedLine);

        var incidents = LinuxJournalIncidentParser.ParseLines(output);

        Assert.Equal(3, incidents.Count);
        Assert.Equal(LinuxJournalIncidentParser.SourceOomKill, incidents[0].Source);
        Assert.Equal(LinuxJournalIncidentParser.SourceSegfault, incidents[1].Source);
        Assert.Equal(LinuxJournalIncidentParser.SourceUnitFailed, incidents[2].Source);
    }

    [Fact]
    public void ParseLines_empty_output_returns_empty_list()
    {
        Assert.Empty(LinuxJournalIncidentParser.ParseLines(""));
        Assert.Empty(LinuxJournalIncidentParser.ParseLines("   \n  \n"));
    }

    [Fact]
    public void GetNewestTimestamp_finds_the_max_across_lines_regardless_of_classification()
    {
        // InitScopeOnlyNonUnitErrorLine never classifies into an incident,
        // but its timestamp is the newest of the three and must still be found.
        var output = string.Join("\n", OomKillLine, InitScopeOnlyNonUnitErrorLine, SegfaultLine);

        var newest = LinuxJournalIncidentParser.GetNewestTimestamp(output);

        Assert.NotNull(newest);
        var expected = DateTime.UnixEpoch.AddTicks(1723000003700000L * 10);
        Assert.Equal(expected, newest);
    }

    [Fact]
    public void GetNewestTimestamp_empty_or_unparseable_output_returns_null()
    {
        Assert.Null(LinuxJournalIncidentParser.GetNewestTimestamp(""));
        Assert.Null(LinuxJournalIncidentParser.GetNewestTimestamp("not json"));
    }
}
