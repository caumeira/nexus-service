using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lifecycle;
using Xunit;
using static Nexus.Service.Lifecycle.DatabaseLayoutMigration;

namespace Nexus.Service.Tests;

public sealed class DatabaseLayoutMigrationTests : IDisposable
{
    private readonly string _base;

    public DatabaseLayoutMigrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "nexus-dbmigtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
    }

    private string P(params string[] parts) =>
        Path.Combine(new[] { _base }.Concat(parts).ToArray());

    [Fact]
    public void MoveDatabase_moves_the_db_file_when_no_sidecars_exist()
    {
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "DB");

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("DB", File.ReadAllText(newDb));
        Assert.False(File.Exists(oldDb));
    }

    [Fact]
    public void MoveDatabase_moves_wal_and_shm_sidecars_alongside_the_db()
    {
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "DB");
        File.WriteAllText(oldDb + "-wal", "WAL");
        File.WriteAllText(oldDb + "-shm", "SHM");

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("DB", File.ReadAllText(newDb));
        Assert.Equal("WAL", File.ReadAllText(newDb + "-wal"));
        Assert.Equal("SHM", File.ReadAllText(newDb + "-shm"));
        Assert.False(File.Exists(oldDb));
        Assert.False(File.Exists(oldDb + "-wal"));
        Assert.False(File.Exists(oldDb + "-shm"));
    }

    [Fact]
    public void MoveDatabase_skips_when_the_target_db_already_exists()
    {
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "OLD");
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        File.WriteAllText(newDb, "NEW");

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("NEW", File.ReadAllText(newDb));
        Assert.True(File.Exists(oldDb)); // untouched, never clobbered nor deleted
    }

    [Fact]
    public void MoveDatabase_treats_a_zero_length_target_db_as_unmigrated_and_overwrites_it()
    {
        // Simulates a store resolving its directory ahead of this migration
        // (testHost does not skip hosted-service startup) and creating an
        // empty file at the target path before any schema write lands.
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "REAL DATA");
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        File.WriteAllBytes(newDb, Array.Empty<byte>());

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("REAL DATA", File.ReadAllText(newDb));
        Assert.False(File.Exists(oldDb));
    }

    [Fact]
    public void MoveDatabase_treats_a_zero_length_target_sidecar_as_unmigrated_and_overwrites_it()
    {
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "DB");
        File.WriteAllText(oldDb + "-wal", "REAL WAL");
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        File.WriteAllBytes(newDb + "-wal", Array.Empty<byte>());

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("REAL WAL", File.ReadAllText(newDb + "-wal"));
        Assert.False(File.Exists(oldDb + "-wal"));
    }

    [Fact]
    public void MoveDatabase_noop_when_the_old_db_is_missing()
    {
        var oldDb = P("does-not-exist.db");
        var newDb = P("db", "temperature.db");

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.False(File.Exists(newDb));
    }

    [Fact]
    public void MoveDatabase_is_idempotent()
    {
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "DB");

        Execute(new[] { new Entry(oldDb, newDb) });
        Execute(new[] { new Entry(oldDb, newDb) }); // second run: old gone -> no-op

        Assert.Equal("DB", File.ReadAllText(newDb));
    }

    [Fact]
    public void MoveDatabase_resumes_when_a_sidecar_already_moved_but_the_db_did_not()
    {
        // Simulates a crash between the sidecar move and the .db move: the
        // wal already landed at the new path, the db is still at the old one.
        var oldDb = P("temperature.db");
        var newDb = P("db", "temperature.db");
        File.WriteAllText(oldDb, "DB");
        File.WriteAllText(oldDb + "-wal", "WAL");
        Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
        File.WriteAllText(newDb + "-wal", "WAL");

        Execute(new[] { new Entry(oldDb, newDb) });

        Assert.Equal("DB", File.ReadAllText(newDb));
        Assert.Equal("WAL", File.ReadAllText(newDb + "-wal"));
        Assert.False(File.Exists(oldDb));
    }

    [Fact]
    public void Execute_continues_past_a_failing_entry()
    {
        var blocker = P("blocker");
        File.WriteAllText(blocker, "i-am-a-file");
        var oldDb1 = P("temperature.db");
        File.WriteAllText(oldDb1, "1");
        var oldDb2 = P("screentime.db");
        File.WriteAllText(oldDb2, "2");
        var newDb2 = P("db", "screentime.db");

        Execute(new[]
        {
            new Entry(oldDb1, Path.Combine(blocker, "temperature.db")), // throws, caught
            new Entry(oldDb2, newDb2),                                    // must still run
        });

        Assert.Equal("2", File.ReadAllText(newDb2));
        Assert.True(File.Exists(oldDb1)); // first entry did not complete
    }

    [Fact]
    public void BuildEntries_maps_temperature_and_screentime_dbs_into_the_db_subfolder()
    {
        var entries = BuildEntries();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.OldDbPath.EndsWith("temperature.db") && e.NewDbPath.EndsWith(Path.Combine("db", "temperature.db")));
        Assert.Contains(entries, e => e.OldDbPath.EndsWith("screentime.db") && e.NewDbPath.EndsWith(Path.Combine("db", "screentime.db")));
    }
}
