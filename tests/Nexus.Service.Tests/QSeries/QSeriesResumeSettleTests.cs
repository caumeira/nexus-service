using System;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

/// <summary>
/// A host resume always re-enumerates USB, so the transport id always changes -
/// rebooting the panel on that took it off adb for ~32 s per sleep/wake cycle
/// (field logs: reboot 20:15:44, back on adb 20:16:16) and gave the MediaTek
/// driver another chance to bind the composite parent. The suppression must be
/// bounded and must never engage on a run where the host has not resumed, or a
/// genuine reseat would go unrecovered.
/// </summary>
public class QSeriesResumeSettleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 20, 15, 44, TimeSpan.Zero);

    [Fact]
    public void A_run_that_never_resumed_does_not_suppress_the_reseat_reboot() =>
        Assert.False(QSeriesPortWatcher.IsResumeReenumeration(Now, 0, Window));

    [Fact]
    public void The_field_timeline_ten_seconds_after_resume_is_treated_as_re_enumeration()
    {
        // 2026-08-26: resume 20:15:34, transport id 4 -> 5 at 20:15:44.
        var resumedAt = Now.AddSeconds(-10);
        Assert.True(QSeriesPortWatcher.IsResumeReenumeration(Now, resumedAt.UtcTicks, Window));
    }

    [Fact]
    public void A_reseat_past_the_window_still_reboots()
    {
        var resumedAt = Now.AddSeconds(-61);
        Assert.False(QSeriesPortWatcher.IsResumeReenumeration(Now, resumedAt.UtcTicks, Window));
    }

    [Fact]
    public void The_window_is_exclusive_at_its_edge()
    {
        var resumedAt = Now - Window;
        Assert.False(QSeriesPortWatcher.IsResumeReenumeration(Now, resumedAt.UtcTicks, Window));
    }

    [Fact]
    public void A_resume_far_in_the_past_does_not_suppress_a_later_reseat()
    {
        var resumedAt = Now.AddHours(-3);
        Assert.False(QSeriesPortWatcher.IsResumeReenumeration(Now, resumedAt.UtcTicks, Window));
    }
}
