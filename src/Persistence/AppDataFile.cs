using System.Text.Json;

namespace Nexus.Service.Persistence;

/// <summary>
/// On-disk shape of one app-data document: <c>&lt;NexusRoot&gt;/app-data/&lt;appId&gt;/&lt;key&gt;.json</c>.
/// <see cref="Cloud"/> is stripped before the document is ever sent to a
/// client or embedded in a profile archive - it exists only for
/// <c>CloudProfileSyncService</c>'s own CAS bookkeeping against the account's
/// cloud row.
/// </summary>
public sealed class AppDataFile
{
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
    public JsonElement Data { get; set; }
    public AppDataCloudState? Cloud { get; set; }
}

/// <summary>Last known-synced state for one app-data document under one cloud account, mirroring CloudProfileSyncRecord's role for profiles. Scoped to <see cref="AccountId"/> because the file (unlike a profile's sync record, which lives under CloudAccountRecord) is not itself account-keyed: a doc synced under a previous account is treated as never synced when a different account is now signed in.</summary>
public sealed class AppDataCloudState
{
    public string AccountId { get; set; } = "";
    public int Revision { get; set; }
    public string Hash { get; set; } = "";
    public string SyncedAt { get; set; } = "";

    /// <summary>
    /// The document's true edit time as of this sync record, distinct from
    /// the local file's own <see cref="AppDataFile.UpdatedAt"/>: a pull
    /// stamps UpdatedAt with the write time of the import itself, which is
    /// not when the content was actually edited upstream. A future
    /// newest-wins comparison against unchanged content (Hash still matches)
    /// uses this instead of UpdatedAt, so an import is never mistaken for a
    /// fresh local edit.
    /// </summary>
    public string EditedAt { get; set; } = "";
}
