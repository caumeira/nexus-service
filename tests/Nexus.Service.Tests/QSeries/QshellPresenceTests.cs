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
            "0123456789ABCDEF", knownQSeries: true, coolerPresent: false, classifiedNonQSeries: false));
    }

    [Fact]
    public void Recoverable_for_an_unseen_serial_when_the_panel_cooler_is_on_usb()
    {
        Assert.True(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "0123456789ABCDEF", knownQSeries: false, coolerPresent: true, classifiedNonQSeries: false));
    }

    [Fact]
    public void Not_recoverable_for_a_bare_mediatek_device_with_no_q_series_corroboration()
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "somephone", knownQSeries: false, coolerPresent: false, classifiedNonQSeries: false));
    }

    [Fact]
    public void Not_recoverable_once_classified_a_non_q_series_device()
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            "somephone", knownQSeries: false, coolerPresent: true, classifiedNonQSeries: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Not_recoverable_without_a_serial(string? serial)
    {
        Assert.False(QSeriesPortWatcher.IsRecoverableQSeriesSerial(
            serial!, knownQSeries: true, coolerPresent: true, classifiedNonQSeries: false));
    }
}
