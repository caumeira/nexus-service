using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

/// <summary>
/// The qshell presence verdict gates the escalation reboot, so the classifier must
/// never answer Absent from output that only proves the package service was not
/// reachable, and never Installed without a version.
/// </summary>
public class QshellPresenceTests
{
    [Fact]
    public void Installed_when_dumpsys_reports_a_version_code()
    {
        const string dumpsys = """
            Packages:
              Package [com.hellonexus.qshell] (a1b2c3):
                userId=10123
                versionCode=5 minSdk=24 targetSdk=30
                versionName=0.1.4
            """;
        Assert.Equal(QSeriesPortWatcher.QshellPresence.Installed, QSeriesPortWatcher.ClassifyQshellDumpsys(dumpsys));
    }

    [Fact]
    public void Absent_when_the_package_service_says_it_cannot_find_the_package()
    {
        Assert.Equal(
            QSeriesPortWatcher.QshellPresence.Absent,
            QSeriesPortWatcher.ClassifyQshellDumpsys("Unable to find package: com.hellonexus.qshell"));
    }

    // Early panel boot: answered with a clean exit while the package service is
    // still scanning. Absent here would latch the suppression on an installed qshell.
    [Theory]
    [InlineData("Can't find service: package")]
    [InlineData("can't find service: package")]
    public void Unknown_while_the_package_service_is_not_up(string output)
    {
        Assert.Equal(QSeriesPortWatcher.QshellPresence.Unknown, QSeriesPortWatcher.ClassifyQshellDumpsys(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something entirely unexpected")]
    public void Unknown_for_empty_or_unrecognized_output(string output)
    {
        Assert.Equal(QSeriesPortWatcher.QshellPresence.Unknown, QSeriesPortWatcher.ClassifyQshellDumpsys(output));
    }

    // A truncated read over a marginal USB-FFS link is not an uninstall.
    [Fact]
    public void Unknown_when_output_is_truncated_before_the_version_line()
    {
        Assert.Equal(
            QSeriesPortWatcher.QshellPresence.Unknown,
            QSeriesPortWatcher.ClassifyQshellDumpsys("Packages:\n  Package [com.hellonexus.qshell] (a1b2c3):\n    userId=10123"));
    }
}

/// <summary>
/// 0x0E8D is MediaTek's whole vendor id, so the invisible-panel recovery must not
/// devnode-reset a phone that happens to be plugged in.
/// </summary>
public class RecoverableQSeriesSerialTests
{
    [Fact]
    public void Recoverable_when_the_serial_was_seen_online_as_q_series()
    {
        Assert.True(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "0123456789ABCDEF", knownQSeries: true, classifiedNonQSeries: false));
    }

    // A cooler on USB is not proof the MediaTek node beside it is the panel, and a
    // serial never seen online is indistinguishable from a panel still enumerating
    // after a cold boot - resetting either is worse than declining to rescue.
    [Fact]
    public void Not_recoverable_for_a_serial_never_seen_online_this_run()
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "0123456789ABCDEF", knownQSeries: false, classifiedNonQSeries: false));
    }

    [Fact]
    public void Not_recoverable_for_a_bare_mediatek_device_with_no_q_series_corroboration()
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "somephone", knownQSeries: false, classifiedNonQSeries: false));
    }

    [Fact]
    public void Not_recoverable_once_classified_a_non_q_series_device()
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "somephone", knownQSeries: true, classifiedNonQSeries: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Not_recoverable_without_a_serial(string? serial)
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            serial!, knownQSeries: true, classifiedNonQSeries: false));
    }
}

/// <summary>
/// The classifier's Absent literals are not backed by a capture from a real Q60, so an
/// unrecognised phrasing leaves the verdict Unknown. `am start` reporting the activity
/// missing IS field-observed and stands in - but only past the grace, or it recreates
/// the latch the probe was written to remove.
/// </summary>
public class QshellAbsentBackstopTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(180);

    [Fact]
    public void An_unrecognized_dumpsys_phrasing_still_classifies_as_unknown()
    {
        Assert.Equal(
            QSeriesPortWatcher.QshellPresence.Unknown,
            QSeriesPortWatcher.ClassifyQshellDumpsys("Failure [not installed for user 0]"));
    }

    [Theory]
    [InlineData("Unable to find package: com.hellonexus.qshell")]
    [InlineData("Unable to find package com.hellonexus.qshell")]
    public void Known_absent_phrasings_classify_as_absent(string output)
    {
        Assert.Equal(QSeriesPortWatcher.QshellPresence.Absent, QSeriesPortWatcher.ClassifyQshellDumpsys(output));
    }

    // The field failure: a reboot at T, "does not exist" at T+31s, qshell installed.
    // Suppressing there disabled the panel's only recovery for the rest of the run.
    [Fact]
    public void A_report_inside_the_grace_does_not_suppress_the_escalation_reboot()
    {
        Assert.False(QSeriesPortWatcher.PreInstallSuppressesEscalation(
            activityMissing: true, sinceFirstSeen: TimeSpan.FromSeconds(31), grace: Grace));
    }

    [Fact]
    public void A_report_still_standing_past_the_grace_suppresses_it()
    {
        Assert.True(QSeriesPortWatcher.PreInstallSuppressesEscalation(
            activityMissing: true, sinceFirstSeen: Grace, grace: Grace));
    }

    [Fact]
    public void A_panel_whose_am_start_succeeded_never_suppresses_however_long_it_has_been_up()
    {
        Assert.False(QSeriesPortWatcher.PreInstallSuppressesEscalation(
            activityMissing: false, sinceFirstSeen: TimeSpan.FromHours(4), grace: Grace));
    }
}
