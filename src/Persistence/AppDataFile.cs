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

/// <summary>Last known-synced state for one app-data document under the active cloud account, mirroring CloudProfileSyncRecord's role for profiles.</summary>
public sealed class AppDataCloudState
{
    public int Revision { get; set; }
    public string Hash { get; set; } = "";
    public string SyncedAt { get; set; } = "";
}
