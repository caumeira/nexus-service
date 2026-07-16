using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MonitoringHistoryRouteResponseTests
{
    private static readonly IReadOnlyDictionary<string, string> NoLuids = new Dictionary<string, string>();

    private static MetricSample Scalars(long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    [Fact]
    public void BuildHistoryResponse_ReportsRetentionDaysAndStepSeconds()
    {
        var response = MonitoringHistoryRoutes.BuildHistoryResponse(
            Array.Empty<MetricSample>(), Array.Empty<MetricSample>(), 0, 600, 600, null, NoLuids);

        Assert.True(response.Supported);
        Assert.Equal(MetricsHistory.RetentionDays, response.RetentionDays);
        Assert.Equal(1, response.StepSeconds); // 600s window / 600 maxPoints -> 1s/point
    }

    [Fact]
    public void BuildHistoryResponse_AlwaysIncludesTheFiveFixedScalarSeries_WhenUnfiltered()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 10, 600, null, NoLuids);

        var ids = response.Series.Select(s => s.Id).ToHashSet();
        Assert.Contains("cpu", ids);
        Assert.Contains("memory", ids);
        Assert.Contains("net-in", ids);
        Assert.Contains("net-out", ids);
        Assert.Contains("cpu-temp", ids);
    }

    [Fact]
    public void BuildHistoryResponse_ConvertsTimestampsToUtcMilliseconds()
    {
        var db = new[] { Scalars(1000, cpu: 50) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 1000, 1000, 600, new HashSet<string> { "cpu" }, NoLuids);

        var cpu = Assert.Single(response.Series);
        var point = Assert.Single(cpu.Points);
        Assert.Equal(1_000_000, point.T); // 1000s -> 1,000,000ms
    }

    [Fact]
    public void BuildHistoryResponse_RoundsPercentAndTempSeriesToOneDecimal()
    {
        var db = new[] { Scalars(0, cpu: 42.345) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(42.3, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_RoundsNetSeriesToWholeNumbers()
    {
        var db = new[] { Scalars(0, netIn: 1234.7) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "net-in" }, NoLuids);

        Assert.Equal(1235, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_TailSampleOverridesDbSampleAtTheSameTimestamp()
    {
        var db = new[] { Scalars(0, cpu: 10) };
        var tail = new[] { Scalars(0, cpu: 90) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, tail, 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(90, Assert.Single(response.Series).Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_SeriesFilter_MatchesExactIds()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "cpu" }, NoLuids);

        Assert.Equal(new[] { "cpu" }, response.Series.Select(s => s.Id));
    }

    [Fact]
    public void BuildHistoryResponse_GpuSeries_PairsLoadAndTemp_KeyedByGpuId()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62) }, Array.Empty<FanReading>()),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        var gpuLoad = response.Series.Single(s => s.Id == "gpu:gpu-0");
        Assert.Equal("gpu", gpuLoad.Kind);
        Assert.Equal("RTX 5080", gpuLoad.Name);
        Assert.Equal(55, gpuLoad.Points.Single().Avg);

        var gpuTemp = response.Series.Single(s => s.Id == "gpu-temp:gpu-0");
        Assert.Equal("gpu-temp", gpuTemp.Kind);
        Assert.Equal(62, gpuTemp.Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_GpuSeries_CarriesAdapterLuid_WhenKnown()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62) }, Array.Empty<FanReading>()),
        };
        var luids = new Dictionary<string, string> { ["gpu-0"] = "10:20" };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, luids);

        Assert.Equal("10:20", response.Series.Single(s => s.Id == "gpu:gpu-0").AdapterLuid);
        Assert.Null(response.Series.Single(s => s.Id == "cpu").AdapterLuid);
    }

    [Fact]
    public void BuildHistoryResponse_KindFilter_MatchesAllSeriesOfThatKind()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                new[] { new GpuReading("gpu-0", "RTX 5080", "", 55, 62), new GpuReading("gpu-1", "RX 7900", "", 30, 40) },
                Array.Empty<FanReading>()),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, new HashSet<string> { "gpu" }, NoLuids);

        Assert.Equal(2, response.Series.Count);
        Assert.All(response.Series, s => Assert.Equal("gpu", s.Kind));
    }

    [Fact]
    public void BuildHistoryResponse_FanSeries_PairsRpmAndDuty_KeyedByFanId()
    {
        var db = new[]
        {
            new MetricSample(0, null, null, null, null, null,
                Array.Empty<GpuReading>(), new[] { new FanReading("fan-0", "Fan 1", 1200, 45) }),
        };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        var rpm = response.Series.Single(s => s.Id == "fan:fan-0");
        Assert.Equal("fan", rpm.Kind);
        Assert.Equal(1200, rpm.Points.Single().Avg);

        var duty = response.Series.Single(s => s.Id == "fan-duty:fan-0");
        Assert.Equal("fan-duty", duty.Kind);
        Assert.Equal(45, duty.Points.Single().Avg);
    }

    [Fact]
    public void BuildHistoryResponse_NoGpuOrFanSeries_WhenNoneEverAppeared()
    {
        var db = new[] { Scalars(0) };

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(db, Array.Empty<MetricSample>(), 0, 0, 600, null, NoLuids);

        Assert.DoesNotContain(response.Series, s => s.Kind is "gpu" or "gpu-temp" or "fan" or "fan-duty");
    }

    [Fact]
    public void BuildPrivacyResponse_ReportsRetentionDays()
    {
        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(Array.Empty<PrivacySession>(), 0, 1000);

        Assert.True(response.Supported);
        Assert.Equal(PrivacyAccess.RetentionDays, response.RetentionDays);
    }

    [Fact]
    public void BuildPrivacyResponse_ConvertsSecondsToMilliseconds()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 1000, 1080) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 2000);

        var session = Assert.Single(response.Sessions);
        Assert.Equal("app.exe", session.App);
        Assert.Equal("microphone", session.Capability);
        Assert.Equal(1_000_000, session.Start);
        Assert.Equal(1_080_000, session.End);
    }

    [Fact]
    public void BuildPrivacyResponse_KeepsAnOpenSessionsEndAsNull()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 1000, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 2000);

        Assert.Null(Assert.Single(response.Sessions).End);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAClosedSessionEntirelyBeforeTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 100, 200) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 1000, 2000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAClosedSessionEntirelyAfterTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 5000, 5100) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 1000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_IncludesAClosedSessionThatPartiallyOverlapsTheWindow()
    {
        var sessions = new[] { new PrivacySession("app.exe", "microphone", 500, 1500) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 1000, 2000);

        Assert.Single(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_IncludesAnOpenSession_WhenItStartsBeforeTheWindowEnd()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 100, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 5000, 10_000);

        Assert.Single(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_ExcludesAnOpenSession_WhenItStartsAfterTheWindowEnd()
    {
        var sessions = new[] { new PrivacySession("app.exe", "webcam", 20_000, null) };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 10_000);

        Assert.Empty(response.Sessions);
    }

    [Fact]
    public void BuildPrivacyResponse_OrdersSessionsByStart()
    {
        var sessions = new[]
        {
            new PrivacySession("app.exe", "microphone", 2000, 2100),
            new PrivacySession("app.exe", "microphone", 1000, 1100),
        };

        var response = MonitoringHistoryRoutes.BuildPrivacyResponse(sessions, 0, 10_000);

        Assert.Equal(new long[] { 1_000_000, 2_000_000 }, response.Sessions.Select(s => s.Start).ToArray());
    }
}
