using System;
using System.Linq;
using Nexus.Service.Diagnostics.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class CoolingStallDetectorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Pump_StallsAfterSustainedZeroRpm_ThenRecoveryClears()
    {
        var det = new CoolingStallDetector();
        det.Observe("pump1", "Pump", "pump", 2100, 60, T0);
        det.Observe("pump1", "Pump", "pump", 0, 60, T0);
        Assert.Equal(CoolingStallStatuses.Ok, det.Snapshot().Devices.Single().Status);

        det.Observe("pump1", "Pump", "pump", 0, 60, T0.AddSeconds(35));
        Assert.Equal(CoolingStallStatuses.Stalled, det.Snapshot().Devices.Single().Status);

        det.Observe("pump1", "Pump", "pump", 2200, 60, T0.AddSeconds(36));
        var snap = det.Snapshot().Devices.Single();
        Assert.Equal(CoolingStallStatuses.Ok, snap.Status);
        Assert.Null(snap.SinceUtc);
    }

    [Fact]
    public void Fan_ZeroRpm_BelowStallDuty_NeverStalls()
    {
        var det = new CoolingStallDetector();
        det.Observe("fan1", "Fan", "fan", 1200, 20, T0);
        det.Observe("fan1", "Fan", "fan", 0, 20, T0);
        // Past the 90s stall-sustain window but duty (20) is below the 30% stall
        // floor, so this must never reach "stalled" - only the slower 5min
        // suspect rule (duty >= 15) could ever fire for this duty, and 100s
        // is well short of that too.
        det.Observe("fan1", "Fan", "fan", 0, 20, T0.AddSeconds(100));
        Assert.Equal(CoolingStallStatuses.Ok, det.Snapshot().Devices.Single().Status);
    }

    [Fact]
    public void Fan_ZeroRpm_AboveSuspectDuty_BecomesSuspectAfterFiveMinutes()
    {
        var det = new CoolingStallDetector();
        det.Observe("fan1", "Fan", "fan", 1200, 20, T0);
        det.Observe("fan1", "Fan", "fan", 0, 20, T0);
        det.Observe("fan1", "Fan", "fan", 0, 20, T0.AddMinutes(5).AddSeconds(1));
        Assert.Equal(CoolingStallStatuses.Suspect, det.Snapshot().Devices.Single().Status);
    }

    [Theory]
    [InlineData(29, CoolingStallStatuses.Ok)]
    [InlineData(31, CoolingStallStatuses.Stalled)]
    public void Pump_SustainedWindowBoundary(int seconds, string expected)
    {
        var det = new CoolingStallDetector();
        det.Observe("pump1", "Pump", "pump", 2100, 60, T0);
        det.Observe("pump1", "Pump", "pump", 0, 60, T0);
        det.Observe("pump1", "Pump", "pump", 0, 60, T0.AddSeconds(seconds));
        Assert.Equal(expected, det.Snapshot().Devices.Single().Status);
    }

    [Fact]
    public void NullRpm_NeverReportedNonzero_NeverFlagged()
    {
        // Unknown-status channels are excluded from Snapshot() entirely (they
        // are probably not connected), so this never appears in the device list.
        var det = new CoolingStallDetector();
        det.Observe("fan2", "Fan", "fan", null, 80, T0);
        Assert.Empty(det.Snapshot().Devices);

        det.Observe("fan2", "Fan", "fan", null, 80, T0.AddMinutes(10));
        Assert.Empty(det.Snapshot().Devices);
    }

    [Fact]
    public void NullRpm_AfterPreviouslyNonzero_CountsAsZeroForStallPurposes()
    {
        var det = new CoolingStallDetector();
        det.Observe("fan3", "Fan", "fan", 1400, 60, T0);
        det.Observe("fan3", "Fan", "fan", null, 60, T0);
        det.Observe("fan3", "Fan", "fan", null, 60, T0.AddSeconds(95));
        Assert.Equal(CoolingStallStatuses.Stalled, det.Snapshot().Devices.Single().Status);
    }

    [Fact]
    public void NoDutySignal_NeverFlagged()
    {
        // Unknown-status channels are excluded from Snapshot() entirely.
        var det = new CoolingStallDetector();
        det.Observe("fan4", "Fan", "fan", 0, null, T0);
        det.Observe("fan4", "Fan", "fan", 0, null, T0.AddMinutes(20));
        Assert.Empty(det.Snapshot().Devices);
    }

    [Fact]
    public void ZeroRpm_FirstObservation_NeverReportedNonzero_NeverFlagged()
    {
        // FanChannel.Rpm is a non-nullable int, so an unpopulated header reports
        // a literal 0, not null. A first-ever observation of 0 rpm at a duty
        // that would otherwise sustain into "stalled" must stay unknown - there
        // is no prior nonzero reading to call this a stall against. Unknown
        // channels are excluded from Snapshot() (probably not connected).
        var det = new CoolingStallDetector();
        det.Observe("fan5", "Fan", "fan", 0, 60, T0);
        Assert.Empty(det.Snapshot().Devices);

        det.Observe("fan5", "Fan", "fan", 0, 60, T0.AddMinutes(10));
        Assert.Empty(det.Snapshot().Devices);
    }

    [Fact]
    public void NeverSpunChannel_AbsentFromSnapshot_ThenAppearsOnceItSpins()
    {
        var det = new CoolingStallDetector();
        det.Observe("fan8", "Fan", "fan", 0, 50, T0);
        Assert.Empty(det.Snapshot().Devices);

        det.Observe("fan8", "Fan", "fan", 1200, 50, T0.AddSeconds(5));
        var device = Assert.Single(det.Snapshot().Devices);
        Assert.Equal("fan8", device.Id);
        Assert.Equal(CoolingStallStatuses.Ok, device.Status);
    }

    [Fact]
    public void StaleChannel_PrunedAfterTenMinutesUnobserved()
    {
        var det = new CoolingStallDetector();
        det.Observe("fan6", "Fan", "fan", 1200, 50, T0);
        Assert.Single(det.Snapshot().Devices);

        // A different channel's observation, 11 minutes later, advances the
        // detector's internal clock past fan6's staleness window.
        det.Observe("fan7", "Fan", "fan", 1200, 50, T0.AddMinutes(11));

        Assert.DoesNotContain(det.Snapshot().Devices, d => d.Id == "fan6");
        Assert.Contains(det.Snapshot().Devices, d => d.Id == "fan7");
    }
}
