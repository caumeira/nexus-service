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
///   local missing, cloud exists,  has record   -> DeleteRemote (deleted locally, tell the cloud)
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
        if (!localExists && cloudExists)
        {
            return hasSyncRecord ? CloudSyncAction.DeleteRemote : CloudSyncAction.Pull;
        }

        // Both exist. Never synced before but both already present (shouldn't
        // normally happen - a fresh pull always records sync state - but pull
        // rather than guess which side is newer).
        if (!hasSyncRecord)
        {
            return CloudSyncAction.Pull;
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
