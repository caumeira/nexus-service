using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

/// <summary>
/// A pre-round-2 database's app_series has no COLLATE NOCASE, so a
/// case-duplicate app (observed under two different casings before the
/// fix shipped) sits as two separate rows with samples split across both
/// keys. Reopening that exact legacy shape with the current store must
/// merge them into one NOCASE row, keep the first-seen casing, and
/// repoint every app_*_seconds row so no history is lost.
/// </summary>
public class AppSeriesNoCaseMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public AppSeriesNoCaseMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-appseriesmigration-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Verbatim pre-round-2 DDL: app_series.name has no COLLATE NOCASE.
    private void CreateLegacyDatabase()
    {
        using var connection = SqliteStores.OpenConnection(_dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE app_series (
                key  INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE app_cpu_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                value_x10 INTEGER,
                PRIMARY KEY (ts, app)
            ) WITHOUT ROWID;
            CREATE TABLE app_mem_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                value_x10 INTEGER,
                PRIMARY KEY (ts, app)
            ) WITHOUT ROWID;
            CREATE TABLE app_gpu_seconds (
                ts        INTEGER NOT NULL,
                app       INTEGER NOT NULL,
                gpu       INTEGER NOT NULL,
                value_x10 INTEGER,
                vram_mb   INTEGER,
                PRIMARY KEY (ts, app, gpu)
            ) WITHOUT ROWID;
            CREATE TABLE schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT INTO schema_meta (key, value) VALUES ('version', '1');

            INSERT INTO app_series (key, name) VALUES (1, 'Chrome.exe');
            INSERT INTO app_series (key, name) VALUES (2, 'chrome.exe');
            INSERT INTO app_series (key, name) VALUES (3, 'notepad.exe');

            INSERT INTO app_cpu_seconds (ts, app, value_x10) VALUES (1000, 1, 100);
            INSERT INTO app_cpu_seconds (ts, app, value_x10) VALUES (1005, 2, 300);
            INSERT INTO app_cpu_seconds (ts, app, value_x10) VALUES (1010, 3, 50);
        """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Reopening_MergesCaseDuplicateAppRows_KeepingFirstSeenCasing()
    {
        CreateLegacyDatabase();

        using var store = new SqliteMetricsHistoryStore(_dbPath);

        var top = store.QueryTopApps("cpu", 0, 10_000, 15);
        Assert.Equal(2, top.Count); // Chrome.exe/chrome.exe merged into one, notepad.exe separate
        var chrome = top.Single(a => string.Equals(a.Name, "chrome.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Chrome.exe", chrome.Name); // key=1 (lowest, first-seen) casing wins
    }

    [Fact]
    public void Reopening_RepointsHistory_FromBothMergedKeys()
    {
        CreateLegacyDatabase();

        using var store = new SqliteMetricsHistoryStore(_dbPath);

        var points = store.QueryAppSeries("cpu", "chrome.exe", 0, 10_000);
        Assert.Equal(new long[] { 1000, 1005 }, points.Select(p => p.TsSec).OrderBy(t => t).ToArray());
        Assert.Contains(points, p => p.Value == 10); // 100/10 = 10.0%
        Assert.Contains(points, p => p.Value == 30); // 300/10 = 30.0%
    }

    [Fact]
    public void Reopening_ThenAppending_ResolvesAThirdCasing_ToTheMergedKey()
    {
        CreateLegacyDatabase();

        using var store = new SqliteMetricsHistoryStore(_dbPath);
        store.Append(new[]
        {
            new AppUsageTick(2000, new[]
            {
                new AppMetricSample("cpu", new[] { new AppUsagePoint("CHROME.EXE", 60, null) }),
            }),
        }, null);

        var points = store.QueryAppSeries("cpu", "chrome.exe", 0, 10_000);
        Assert.Equal(3, points.Count); // the two migrated rows plus this new one, all one app
    }

    [Fact]
    public void Reopening_BumpsSchemaVersion_SoTheMigrationRunsOnlyOnce()
    {
        CreateLegacyDatabase();

        using (var first = new SqliteMetricsHistoryStore(_dbPath))
        {
            Assert.Equal(2, first.QueryTopApps("cpu", 0, 10_000, 15).Count);
        }

        // A second reopen must not re-run the merge against already-merged
        // (now NOCASE-unique, non-duplicate) data.
        using var second = new SqliteMetricsHistoryStore(_dbPath);
        Assert.Equal(2, second.QueryTopApps("cpu", 0, 10_000, 15).Count);
    }

    [Fact]
    public void FreshDatabase_SkipsMigrationWork_AndIsAlreadyNoCase()
    {
        using var store = new SqliteMetricsHistoryStore(_dbPath);
        store.Append(new[]
        {
            new AppUsageTick(0, new[]
            {
                new AppMetricSample("cpu", new[] { new AppUsagePoint("App.exe", 10, null) }),
            }),
            new AppUsageTick(1, new[]
            {
                new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 30, null) }),
            }),
        }, null);

        var top = Assert.Single(store.QueryTopApps("cpu", 0, 10_000, 15));
        Assert.Equal("App.exe", top.Name);
        Assert.Equal(20, top.Avg);
    }
}
