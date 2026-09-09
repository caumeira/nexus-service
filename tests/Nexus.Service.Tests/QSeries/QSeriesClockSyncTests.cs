using Nexus.Service.QSeries;

namespace Nexus.Service.Tests.QSeries;

/// <summary>
/// The clock sync used to log time=ok whenever `cmd alarm set-time` printed
/// nothing, which a quietly refused call also does. These pin the readback
/// comparison that replaced that signal: only a parsed epoch inside tolerance
/// is success.
/// </summary>
public class EvaluateClockReadbackTests
{
    private const long Host = 1_757_000_000;

    [Theory]
    [InlineData("1757000000", 0)]
    [InlineData("1757000010", 10)]
    [InlineData("1756999990", -10)]
    [InlineData("  1757000003\n", 3)]
    public void In_tolerance_is_ok(string output, long expectedDrift)
    {
        var check = QSeriesPortWatcher.EvaluateClockReadback(output, Host, 10, out var drift);

        Assert.Equal(QSeriesPortWatcher.ClockSyncCheck.Ok, check);
        Assert.Equal(expectedDrift, drift);
    }

    [Theory]
    [InlineData("1757000011", 11)]
    [InlineData("1756999989", -11)]
    // A stuck RTC years off is the failure that motivated the sync.
    [InlineData("1483228800", 1483228800 - Host)]
    public void Out_of_tolerance_is_drifted(string output, long expectedDrift)
    {
        var check = QSeriesPortWatcher.EvaluateClockReadback(output, Host, 10, out var drift);

        Assert.Equal(QSeriesPortWatcher.ClockSyncCheck.Drifted, check);
        Assert.Equal(expectedDrift, drift);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/system/bin/sh: date: not found")]
    [InlineData("cmd: Failure calling service alarm")]
    [InlineData("0")]
    [InlineData("-1757000000")]
    [InlineData("1757000000 extra")]
    [InlineData("1.757e9")]
    [InlineData("99999999999999999999999999")]
    public void Unparseable_output_is_never_success(string output)
    {
        var check = QSeriesPortWatcher.EvaluateClockReadback(output, Host, 10, out var drift);

        Assert.Equal(QSeriesPortWatcher.ClockSyncCheck.Unverified, check);
        Assert.Equal(0, drift);
    }

    /// <summary>A device clock at long.MaxValue must classify, not overflow.</summary>
    [Fact]
    public void Absurdly_large_epoch_is_drifted_not_thrown()
    {
        var check = QSeriesPortWatcher.EvaluateClockReadback(
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Host, 10, out var drift);

        Assert.Equal(QSeriesPortWatcher.ClockSyncCheck.Drifted, check);
        Assert.True(drift > 0);
    }
}

/// <summary>
/// The OS-shutdown screen-off runs inside Program.FastServiceShutdown's parallel
/// Task.WaitAll. `input keyevent` execs a JVM on the panel and was measured at
/// 1674 ms, so both the per-serial cap and the outer teardown cap have to clear
/// it or the process exits mid-keyevent - the shape of the bug this replaced.
///
/// FastServiceShutdown is a WINDOWS-gated local function, so the wiring itself
/// is pinned by reading the source: the defect being fixed was a teardown path
/// nothing ever called, which no runtime assertion on this host can catch.
/// </summary>
public class QSeriesShutdownBudgetTests
{
    private static readonly TimeSpan MeasuredKeyeventCost = TimeSpan.FromMilliseconds(1674);

    [Fact]
    public void FastServiceShutdown_calls_the_panel_sleep()
    {
        var body = FastServiceShutdownBody();

        Assert.Contains("SleepPanelsForOsShutdown", body, StringComparison.Ordinal);
        Assert.Contains("OsShutdownTeardownBudget", body, StringComparison.Ordinal);
    }

    /// <summary>The panel must not be blanked on a stop the service returns from:
    /// an OTA install and a GPU-change restart both repaint moments later.</summary>
    [Fact]
    public void Panel_sleep_is_gated_on_os_shutdown()
    {
        var body = FastServiceShutdownBody();

        // Between the local's declaration and the call, so the declaration itself
        // cannot satisfy the assertion: searching the whole body for the
        // identifier passes even with the gate flattened.
        var declaration = body.IndexOf("var osShutdown", StringComparison.Ordinal);
        Assert.True(declaration >= 0, "osShutdown local not found");
        var call = body.IndexOf("SleepPanelsForOsShutdown", StringComparison.Ordinal);
        Assert.True(call > declaration, "SleepPanelsForOsShutdown not found after the osShutdown local");

        var betweenDeclarationAndCall = body[(declaration + "var osShutdown".Length)..call];
        Assert.Contains("if (osShutdown)", betweenDeclarationAndCall, StringComparison.Ordinal);
    }

    private static string FastServiceShutdownBody()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Program.cs"));
        var start = source.IndexOf("static void FastServiceShutdown(", StringComparison.Ordinal);
        Assert.True(start >= 0, "FastServiceShutdown not found in Program.cs");
        // Local function at file scope, so its terminator is the first brace in
        // column zero after it.
        var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "FastServiceShutdown body not terminated");
        var body = source[start..end];

        // A raw string or #if carrying a column-zero brace would truncate the
        // body and quietly turn every assertion above into a search over a
        // fragment; the teardown log line is the function's last statement.
        Assert.Contains("[shutdown] fast teardown", body, StringComparison.Ordinal);
        return body;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Shutdown_keyevent_cap_clears_the_measured_keyevent_cost()
    {
        Assert.True(QSeriesPortWatcher.ShutdownKeyeventTimeout > MeasuredKeyeventCost);
    }

    [Fact]
    public void Os_shutdown_teardown_budget_clears_the_keyevent_cap()
    {
        Assert.True(
            Nexus.Service.Lifecycle.HostShutdown.OsShutdownTeardownBudget
            > QSeriesPortWatcher.ShutdownKeyeventTimeout);
    }

    /// <summary>A stop that is not the OS going down skips the panel work, so it
    /// keeps the shorter cap: an OTA install and a GPU-change restart both come
    /// back immediately and must not pay for the panel.</summary>
    [Fact]
    public void Non_os_stop_keeps_the_shorter_budget()
    {
        Assert.True(
            Nexus.Service.Lifecycle.HostShutdown.FastTeardownBudget
            < Nexus.Service.Lifecycle.HostShutdown.OsShutdownTeardownBudget);
    }

    /// <summary>The serial loop is sequential, so a second panel must come out of
    /// a shared budget rather than adding another full per-serial cap.</summary>
    [Fact]
    public void Sleep_budget_bounds_the_whole_serial_loop()
    {
        Assert.True(QSeriesPortWatcher.ShutdownSleepBudget >= QSeriesPortWatcher.ShutdownKeyeventTimeout);
        Assert.True(
            QSeriesPortWatcher.ShutdownSleepBudget
            < Nexus.Service.Lifecycle.HostShutdown.OsShutdownTeardownBudget);
    }
}
