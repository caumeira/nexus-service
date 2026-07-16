using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Microsoft.Data.Sqlite;

namespace Nexus.Service.Activity.Storage;

/// <summary>
/// SQLite-backed persistent store for screen-time sessions. One file per install,
/// next to settings.json under the platform config dir. WAL mode on so writes
/// (one per focus change) don't block reads from the API thread.
///
/// Schema: single `sessions` table with denormalised date_local / hour_local
/// columns derived at write time. SQLite has no first-class timezone handling
/// and we want "show me Tuesday" to mean Tuesday in the user's wall clock;
/// caching the local date at insert time avoids doing datetime conversion in
/// every read query and keeps DST boundaries sane.
/// </summary>
public sealed class SqliteScreenTimeStore : IScreenTimeStore
{
    private const string DateFormat = "yyyy-MM-dd";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    public SqliteScreenTimeStore() : this(ResolveDatabasePath()) { }

    public SqliteScreenTimeStore(string dbPath)
    {
        _dbPath = dbPath;
        _connection = SqliteStores.OpenConnection(dbPath);
        EnsureSchema();
    }

    public string DatabasePath => _dbPath;

    public void RecordSession(string appName, string? appPath, long startedUtcMs, long endedUtcMs)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return;
        }
        if (endedUtcMs <= startedUtcMs)
        {
            return;
        }

        var startedLocal = DateTimeOffset.FromUnixTimeMilliseconds(startedUtcMs).LocalDateTime;
        var dateLocal = startedLocal.ToString(DateFormat);
        var hourLocal = startedLocal.Hour;
        var duration = endedUtcMs - startedUtcMs;

        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (app_name, app_path, started_utc, ended_utc, duration_ms, date_local, hour_local)
                VALUES ($app, $path, $start, $end, $dur, $date, $hour);
            """;
            cmd.Parameters.AddWithValue("$app", appName);
            cmd.Parameters.AddWithValue("$path", (object?)appPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", startedUtcMs);
            cmd.Parameters.AddWithValue("$end", endedUtcMs);
            cmd.Parameters.AddWithValue("$dur", duration);
            cmd.Parameters.AddWithValue("$date", dateLocal);
            cmd.Parameters.AddWithValue("$hour", hourLocal);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FocusSessionRow> QuerySessions(long fromUtcMs, long toUtcMs)
    {
        var rows = new List<FocusSessionRow>();
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT app_name, app_path, started_utc, ended_utc
                FROM sessions
                WHERE ended_utc >= $from AND started_utc <= $to
                ORDER BY started_utc ASC;
            """;
            cmd.Parameters.AddWithValue("$from", fromUtcMs);
            cmd.Parameters.AddWithValue("$to", toUtcMs);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new FocusSessionRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }
        return rows;
    }

    public DayBreakdown GetDay(DateOnly localDate)
    {
        var dateStr = localDate.ToString(DateFormat);
        var result = new DayBreakdown
        {
            Date = dateStr,
            HourlyMs = new List<long>(new long[24]),
        };

        lock (_writeLock)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT app_name, SUM(duration_ms) AS total, COUNT(*) AS pickups
                    FROM sessions WHERE date_local = $date
                    GROUP BY app_name ORDER BY total DESC;
                """;
                cmd.Parameters.AddWithValue("$date", dateStr);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var total = reader.GetInt64(1);
                    result.Apps.Add(new AppUsage
                    {
                        Name = reader.GetString(0),
                        TotalMs = total,
                    });
                    result.TotalMs += total;
                    result.Pickups += reader.GetInt32(2);
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT hour_local, SUM(duration_ms)
                    FROM sessions WHERE date_local = $date
                    GROUP BY hour_local;
                """;
                cmd.Parameters.AddWithValue("$date", dateStr);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var hour = reader.GetInt32(0);
                    if (hour >= 0 && hour < 24)
                    {
                        result.HourlyMs[hour] = reader.GetInt64(1);
                    }
                }
            }
        }

        return result;
    }

    public IReadOnlyList<DayTotal> GetRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        var result = new List<DayTotal>();
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT date_local, SUM(duration_ms) AS total, COUNT(*) AS pickups
                FROM sessions
                WHERE date_local BETWEEN $from AND $to
                GROUP BY date_local ORDER BY date_local ASC;
            """;
            cmd.Parameters.AddWithValue("$from", fromInclusive.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$to", toInclusive.ToString(DateFormat));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new DayTotal
                {
                    Date = reader.GetString(0),
                    TotalMs = reader.GetInt64(1),
                    Pickups = reader.GetInt32(2),
                });
            }
        }
        return result;
    }

    public AppHistory GetAppHistory(string appName, DateOnly fromInclusive, DateOnly toInclusive)
    {
        var result = new AppHistory { AppName = appName };
        lock (_writeLock)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT date_local, SUM(duration_ms), COUNT(*)
                    FROM sessions
                    WHERE app_name = $app AND date_local BETWEEN $from AND $to
                    GROUP BY date_local ORDER BY date_local ASC;
                """;
                cmd.Parameters.AddWithValue("$app", appName);
                cmd.Parameters.AddWithValue("$from", fromInclusive.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$to", toInclusive.ToString(DateFormat));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var total = reader.GetInt64(1);
                    var pickups = reader.GetInt32(2);
                    result.Daily.Add(new DayTotal
                    {
                        Date = reader.GetString(0),
                        TotalMs = total,
                        Pickups = pickups,
                    });
                    result.TotalMs += total;
                    result.TotalPickups += pickups;
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(MAX(duration_ms), 0)
                    FROM sessions
                    WHERE app_name = $app AND date_local BETWEEN $from AND $to;
                """;
                cmd.Parameters.AddWithValue("$app", appName);
                cmd.Parameters.AddWithValue("$from", fromInclusive.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$to", toInclusive.ToString(DateFormat));
                var longest = cmd.ExecuteScalar();
                result.LongestSessionMs = longest is long l ? l : 0;
            }
        }
        return result;
    }

    public IReadOnlyList<AppUsage> GetTodayUsage(DateOnly today)
    {
        var day = GetDay(today);
        return day.Apps;
    }

    public IReadOnlyList<AppUsage> GetHourUsage(DateOnly localDate, int hourLocal)
    {
        if (hourLocal < 0 || hourLocal > 23)
        {
            return Array.Empty<AppUsage>();
        }

        var result = new List<AppUsage>();
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT app_name, SUM(duration_ms) AS total
                FROM sessions
                WHERE date_local = $date AND hour_local = $hour
                GROUP BY app_name ORDER BY total DESC;
            """;
            cmd.Parameters.AddWithValue("$date", localDate.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$hour", hourLocal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AppUsage
                {
                    Name = reader.GetString(0),
                    TotalMs = reader.GetInt64(1),
                });
            }
        }
        return result;
    }

    public int DeleteDay(DateOnly localDate)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE date_local = $date;";
            cmd.Parameters.AddWithValue("$date", localDate.ToString(DateFormat));
            return cmd.ExecuteNonQuery();
        }
    }

    public int DeleteRange(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE date_local BETWEEN $from AND $to;";
            cmd.Parameters.AddWithValue("$from", fromInclusive.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$to", toInclusive.ToString(DateFormat));
            return cmd.ExecuteNonQuery();
        }
    }

    public int DeleteApp(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return 0;
        }
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE app_name = $app;";
            cmd.Parameters.AddWithValue("$app", appName);
            return cmd.ExecuteNonQuery();
        }
    }

    public int DeleteAll()
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions;";
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        try
        {
            _connection.Close();
            _connection.Dispose();
        }
        catch { }
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                app_name     TEXT    NOT NULL,
                app_path     TEXT,
                started_utc  INTEGER NOT NULL,
                ended_utc    INTEGER NOT NULL,
                duration_ms  INTEGER NOT NULL,
                date_local   TEXT    NOT NULL,
                hour_local   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_date_app  ON sessions(date_local, app_name);
            CREATE INDEX IF NOT EXISTS ix_sessions_date_hour ON sessions(date_local, hour_local);
            CREATE INDEX IF NOT EXISTS ix_sessions_app       ON sessions(app_name);
            CREATE INDEX IF NOT EXISTS ix_sessions_started   ON sessions(started_utc);

            CREATE TABLE IF NOT EXISTS schema_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('version', '1');
        """;
        cmd.ExecuteNonQuery();
    }

    private static string ResolveDatabasePath() =>
        Path.Combine(NexusDataPaths.DatabaseDir(), "screentime.db");
}
