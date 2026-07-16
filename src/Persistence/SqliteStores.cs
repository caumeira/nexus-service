using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Nexus.Service.Persistence;

/// <summary>
/// Shared bootstrap for the service's ADO.NET-backed SQLite stores
/// (temperature, screen time, metrics history): one-time native bundle init,
/// directory creation, connection open, and the WAL/NORMAL/foreign_keys
/// pragma block every store applied identically before this was extracted.
/// Schema (CREATE TABLE) stays per-store since each has its own shape.
/// </summary>
internal static class SqliteStores
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static void EnsureBundleInitialized()
    {
        if (_initialized)
        {
            return;
        }
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }
            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }

    /// <summary>Ensures dbPath's directory exists, opens a connection, and
    /// applies the shared pragma block. Native bundle init runs at most once
    /// per process regardless of how many stores call this.</summary>
    public static SqliteConnection OpenConnection(string dbPath)
    {
        EnsureBundleInitialized();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=OFF;
        """;
        cmd.ExecuteNonQuery();
    }
}
