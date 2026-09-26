using System.Text.Json;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Body of <c>PUT /apps-api/data/{appId}/{key}</c>. Compare-and-swap: the
/// write is rejected with the current document when <see cref="BaseRevision"/>
/// no longer matches what is stored.
/// </summary>
public sealed class AppDataPutRequest
{
    public int BaseRevision { get; set; }
    public JsonElement Data { get; set; }
}

/// <summary>
/// Shape returned by <c>GET /apps-api/data/{appId}/{key}</c> and by a
/// rejected PUT's 409 body (the current document the caller must rebase
/// onto). <see cref="Data"/> is null exactly when <see cref="Revision"/> is 0
/// (no document has ever been written for this app/key).
/// </summary>
public sealed class AppDataDocumentDto
{
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
    public JsonElement? Data { get; set; }
}

/// <summary>Success shape of <c>PUT /apps-api/data/{appId}/{key}</c> - the new revision, no payload echo.</summary>
public sealed class AppDataPutResultDto
{
    public int Revision { get; set; }
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// Shape the service (not nexus-api) sends as the cloud row's <c>payload</c>
/// for account-wide app-data sync. nexus-api's <c>updatedAt</c> on that row is
/// upload time, not edit time - two machines racing to sync would otherwise
/// let whichever one uploads LAST win regardless of which one actually edited
/// last. Wrapping the true edit time here lets CloudProfileSyncService compare
/// edit times instead of upload times. A payload without <see cref="NexusAppData"/>
/// set to 1 is a document from before this envelope existed, or a payload sent
/// some other way; it is read as raw data with editedAt falling back to the
/// row's own updatedAt.
/// </summary>
public sealed class AppDataCloudEnvelope
{
    public int NexusAppData { get; set; } = 1;
    public string EditedAt { get; set; } = "";
    public JsonElement Data { get; set; }
}
