using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Permanent migration: moves temperature.db and screentime.db (plus their
/// -wal/-shm sidecars) from the flat config root into the dedicated db/
/// subfolder every ADO.NET store now shares (NexusDataPaths.DatabaseDir()).
/// Runs immediately after DataLayoutMigration.Run(), before any store opens
/// its file, so a store never creates the new (empty) target ahead of the
/// move.
///
/// Idempotent and best-effort like DataLayoutMigration: a database whose
/// target .db already exists is skipped, and one that fails partway is
/// logged and retried on the next boot (the old file is left in place, never
/// deleted).
/// </summary>
internal static class DatabaseLayoutMigration
{
    private static readonly string[] SidecarSuffixes = { "-wal", "-shm" };

    internal readonly record struct Entry(string OldDbPath, string NewDbPath);

    public static void Run()
    {
        try { Execute(BuildEntries()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[db-layout-migration] aborted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static List<Entry> BuildEntries()
    {
        var oldRoot = NexusDataPaths.NexusRoot();
        var newRoot = NexusDataPaths.DatabaseDir();
        return new List<Entry>
        {
            new(Path.Combine(oldRoot, "temperature.db"), Path.Combine(newRoot, "temperature.db")),
            new(Path.Combine(oldRoot, "screentime.db"), Path.Combine(newRoot, "screentime.db")),
        };
    }

    internal static void Execute(IEnumerable<Entry> entries)
    {
        foreach (var e in entries)
        {
            MoveDatabase(e.OldDbPath, e.NewDbPath);
        }
    }

    // A WAL/SHM sidecar carries not-yet-checkpointed transactions, so both
    // must land next to the moved .db before the .db move itself runs - the
    // .db move is attempted last, making its presence at the new path a
    // single flag for "this database finished moving". If a sidecar move
    // fails, the whole entry aborts (caught below) rather than moving the
    // .db without it, which would silently drop the unmoved sidecar's data.
    private static void MoveDatabase(string oldDbPath, string newDbPath)
    {
        if (File.Exists(newDbPath) || !File.Exists(oldDbPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newDbPath)!);

            foreach (var suffix in SidecarSuffixes)
            {
                var oldSidecar = oldDbPath + suffix;
                var newSidecar = newDbPath + suffix;
                if (File.Exists(oldSidecar) && !File.Exists(newSidecar))
                {
                    File.Move(oldSidecar, newSidecar);
                }
            }

            File.Move(oldDbPath, newDbPath);
            Console.WriteLine($"[db-layout-migration] moved {oldDbPath} -> {newDbPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[db-layout-migration] {oldDbPath} failed (retried next boot): {ex.GetType().Name}: {ex.Message}");
        }
    }
}
