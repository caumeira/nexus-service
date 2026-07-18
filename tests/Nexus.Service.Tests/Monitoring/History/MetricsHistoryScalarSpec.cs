using System;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Monitoring.History.Binary;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// The scalar-field behavior IMetricsHistoryStore.Append/Query must have
/// regardless of which store implements it - x10 scaling, whole-int net,
/// null-vs-absent, same-ts-replace, and the prune-cutoff floor. Run against
/// both SqliteMetricsHistoryStore (SqliteScalarHistorySpecTests) and
/// BinaryMetricsHistoryStore (BinaryScalarHistorySpecTests) so a behavior
/// change to either store's scalar path is pinned once, not twice. GPU/fan/
/// reopen-key-stability cases stay in SqliteMetricsHistoryStoreTests -
/// BinaryMetricsHistoryStore does not implement that surface yet (see its
/// class doc). Privacy-session behavior has its own shared spec,
/// PrivacySessionHistorySpec, since both stores implement it.
/// </summary>
public abstract class MetricsHistoryScalarSpec : IDisposable
{
    private readonly string _dir;
    protected readonly IMetricsHistoryStore Store;

    protected MetricsHistoryScalarSpec()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-metricshistory-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        Store = CreateStore(_dir);
    }

    protected abstract IMetricsHistoryStore CreateStore(string dir);

    public virtual void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MetricSample Scalars(long ts, double? cpu = 50, double? mem = 60, double? netIn = 1000, double? netOut = 500, double? cpuTemp = 55) =>
        new(ts, cpu, mem, netIn, netOut, cpuTemp, Array.Empty<GpuReading>(), Array.Empty<FanReading>());

    [Fact]
    public void Append_then_Query_RoundTripsScalarFields()
    {
        Store.Append(new[] { Scalars(1000, cpu: 42.3, mem: 61.7, netIn: 12345, netOut: 6789, cpuTemp: 55.4) }, null);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal(1000, row.TsSec);
        Assert.Equal(42.3, row.CpuPercent);
        Assert.Equal(61.7, row.MemoryPercent);
        Assert.Equal(12345, row.NetInBytesPerSec);
        Assert.Equal(6789, row.NetOutBytesPerSec);
        Assert.Equal(55.4, row.CpuTempC);
    }

    [Fact]
    public void Append_preserves_null_fields_as_source_failed_not_zero()
    {
        Store.Append(new[] { Scalars(1000, cpu: null, mem: 60, netIn: null, netOut: null, cpuTemp: null) }, null);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Null(row.CpuPercent);
        Assert.Equal(60, row.MemoryPercent);
        Assert.Null(row.NetInBytesPerSec);
        Assert.Null(row.NetOutBytesPerSec);
        Assert.Null(row.CpuTempC);
    }

    [Fact]
    public void Append_SameTimestamp_ReplacesRatherThanDuplicates()
    {
        Store.Append(new[] { Scalars(1000, cpu: 10) }, null);
        Store.Append(new[] { Scalars(1000, cpu: 90) }, null);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal(90, row.CpuPercent);
    }

    [Fact]
    public void Query_ReturnsRowsAscendingByTs_AndExcludesOutOfRange()
    {
        Store.Append(new[] { Scalars(3000), Scalars(1000), Scalars(9000), Scalars(2000) }, null);

        var rows = Store.Query(1500, 3500);

        Assert.Equal(new long[] { 2000, 3000 }, rows.Select(r => r.TsSec).ToArray());
    }

    [Fact]
    public void Append_EmptyList_WithNoCutoff_IsANoOp()
    {
        Store.Append(Array.Empty<MetricSample>(), null);

        Assert.Empty(Store.Query(0, long.MaxValue));
    }

    [Fact]
    public void Append_WithPruneCutoff_ExcludesScalarRowsOlderThanTheCutoff()
    {
        Store.Append(new[] { Scalars(1000, cpu: 10), Scalars(5000, cpu: 20) }, null);

        Store.Append(Array.Empty<MetricSample>(), pruneCutoffSec: 3000);

        var row = Assert.Single(Store.Query(0, 10_000));
        Assert.Equal(5000, row.TsSec);
    }
}

public sealed class SqliteScalarHistorySpecTests : MetricsHistoryScalarSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new SqliteMetricsHistoryStore(Path.Combine(dir, "metrics.db"));
}

public sealed class BinaryScalarHistorySpecTests : MetricsHistoryScalarSpec
{
    protected override IMetricsHistoryStore CreateStore(string dir) =>
        new BinaryMetricsHistoryStore(dir);
}
