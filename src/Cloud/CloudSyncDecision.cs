namespace Nexus.Service.Cloud;

/// <summary>What CloudProfileSyncService should do with one profile on a sync tick.</summary>
public enum CloudSyncAction
{
    /// <summary>Already in sync - nothing to do.</summary>
    None,

    /// <summary>Push the local export payload (create or update on the cloud).</summary>
    Push,

    /// <summary>Fetch the cloud payload and write it locally (create-with-id or overwrite).</summary>
    Pull,

    /// <summary>Both sides changed since the same base revision - surface for a user choice.</summary>
    Conflict,

    /// <summary>Local profile was deleted after having synced - tell the cloud to delete its row.</summary>
    DeleteRemote,

    /// <summary>Cloud row was deleted (from another machine) after having synced - delete the local profile.</summary>
    DeleteLocal,
}

/// <summary>
/// Pure per-profile sync decision. Input is one profile's local/cloud/last-
/// synced state; output is what CloudProfileSyncService should do. Kept
/// side-effect free so the whole decision matrix is unit-testable without an
/// HTTP layer.
///
/// Matrix (mirrors plans/account-system.md "Sync model"). hasSyncRecord is
/// the tombstone signal that distinguishes "never synced" from "synced, then
/// one side deleted it":
///   local missing, cloud missing              -> None (nothing to sync)
///   local exists,  cloud missing, no record    -> Push (new local-only profile)
///   local exists,  cloud missing, has record   -> DeleteLocal (deleted on another machine)
///   local missing, cloud exists,  no record    -> Pull (new cloud profile)
///   local missing, cloud exists                -> None (the backup outlives the local copy; deleting a cloud profile is an explicit user action)
///   both exist,    no record                  -> Push (re-link after a sign-out; local is the source)
///   local clean,   cloud unchanged             -> None
///   local clean,   cloud moved                 -> Pull (silent LWW, cloud wins - local never diverged)
///   local dirty,   cloud unchanged              -> Push (silent LWW, local wins - cloud never diverged)
///   local dirty,   cloud moved                 -> Conflict (both diverged from the same base revision)
/// "Clean" means the current local export hash equals the hash recorded at
/// the last successful sync; "cloud moved" means the cloud revision is ahead
/// of the revision recorded at that same last sync.
/// </summary>
internal static class CloudSyncDecision
{
    public static CloudSyncAction Decide(
        bool localExists,
        string? localHash,
        bool cloudExists,
        int cloudRevision,
        bool hasSyncRecord,
        int syncedRevision,
        string? syncedHash)
    {
        if (!localExists && !cloudExists)
        {
            return CloudSyncAction.None;
        }
        if (localExists && !cloudExists)
        {
            return hasSyncRecord ? CloudSyncAction.DeleteLocal : CloudSyncAction.Push;
        }
        // Deleting a profile locally must NOT destroy its backup: the whole
        // point of the backup is to restore from it afterwards. The cloud copy
        // stays until the user deletes it explicitly, and the UI offers to
        // import it back.
        if (!localExists && cloudExists)
        {
            return CloudSyncAction.None;
        }

        // Both exist with no sync record: this machine has the profile and so
        // does its backup, but the link between them is gone - the ordinary
        // case being a sign-out and sign-in, which drops the sync records with
        // the account. The cloud copy is a BACKUP of this machine, so the
        // machine's own library wins and re-establishes it; pulling here would
        // silently overwrite local work with whatever was last backed up.
        // Restoring a backup on purpose is the import flow, which creates a
        // new profile instead.
        if (!hasSyncRecord)
        {
            return CloudSyncAction.Push;
        }

        var localClean = string.Equals(localHash, syncedHash, System.StringComparison.Ordinal);
        var cloudMoved = cloudRevision > syncedRevision;

        if (localClean && !cloudMoved)
        {
            return CloudSyncAction.None;
        }
        if (localClean && cloudMoved)
        {
            return CloudSyncAction.Pull;
        }
        if (!localClean && !cloudMoved)
        {
            return CloudSyncAction.Push;
        }
        return CloudSyncAction.Conflict;
    }
}
