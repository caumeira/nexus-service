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
