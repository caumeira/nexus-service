using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class QSeriesTransportStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public QSeriesTransportStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-qseries-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "qseries-transports.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var store = new QSeriesTransportStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_when_file_empty()
    {
        File.WriteAllText(_path, string.Empty);
        var store = new QSeriesTransportStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_when_file_corrupt()
    {
        // Corrupt JSON must not crash the watcher on startup; the failure mode
        // is losing the promotion map and re-bootstrapping on the next attach.
        File.WriteAllText(_path, "{not valid json");
        var store = new QSeriesTransportStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Save_then_Load_roundtrips_records()
    {
        var store = new QSeriesTransportStore(_path);
        var input = new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal)
        {
            ["0123456789ABCDEF"] = new QSeriesTransportRecord(
                Model: "HYTE_Q60_Display",
                IpAddress: "192.168.1.42",
                Port: 5555,
                PromotedAt: new DateTimeOffset(2026, 5, 17, 14, 30, 0, TimeSpan.Zero)),
            ["FEDCBA9876543210"] = new QSeriesTransportRecord(
                Model: "HYTE_Q80_Display",
                IpAddress: "10.0.0.7",
                Port: 5555,
                PromotedAt: new DateTimeOffset(2026, 5, 17, 14, 31, 0, TimeSpan.Zero)),
        };
        store.Save(input);

        var roundtrip = store.Load();
        Assert.Equal(2, roundtrip.Count);
        Assert.Equal("192.168.1.42", roundtrip["0123456789ABCDEF"].IpAddress);
        Assert.Equal("HYTE_Q60_Display", roundtrip["0123456789ABCDEF"].Model);
        Assert.Equal(5555, roundtrip["0123456789ABCDEF"].Port);
        Assert.Equal("10.0.0.7", roundtrip["FEDCBA9876543210"].IpAddress);
    }

    [Fact]
    public void Save_overwrites_previous_state()
    {
        var store = new QSeriesTransportStore(_path);
        store.Save(new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal)
        {
            ["A"] = new QSeriesTransportRecord("HYTE_Q60_Display", "192.168.1.1", 5555, DateTimeOffset.UnixEpoch),
        });
        store.Save(new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal)
        {
            ["B"] = new QSeriesTransportRecord("HYTE_Q80_Display", "192.168.1.2", 5555, DateTimeOffset.UnixEpoch),
        });
        var roundtrip = store.Load();
        Assert.Single(roundtrip);
        Assert.Contains("B", roundtrip.Keys);
        Assert.DoesNotContain("A", roundtrip.Keys);
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        // Use a deeply-nested path to confirm the store creates each
        // intermediate directory (matters on dev machines where
        // %ProgramData%\Nexus doesn't exist yet).
        var nestedDir = Path.Combine(_tempDir, "deep", "nested", "dir");
        var nestedPath = Path.Combine(nestedDir, "qseries-transports.json");
        var store = new QSeriesTransportStore(nestedPath);
        store.Save(new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal)
        {
            ["X"] = new QSeriesTransportRecord("HYTE_Q60_Display", "192.168.1.1", 5555, DateTimeOffset.UnixEpoch),
        });
        Assert.True(File.Exists(nestedPath));
    }
}
