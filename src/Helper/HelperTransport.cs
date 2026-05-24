using System.Text.Json;

namespace Nexus.Service.Helper;

/// <summary>
/// Wire envelope for every message on the helper pipe in either direction.
/// <c>Type</c> is the discriminator (<c>&lt;domain&gt;.&lt;verb&gt;</c>, e.g.
/// <c>trayIcon.setVisible</c>, <c>screenTime.session</c>). <c>Id</c> is the
/// correlation id for RPC-style commands; null for one-way pushes.
/// <c>Payload</c> is the raw JSON, decoded by the receiver using the
/// appropriate <c>AppJsonContext</c> entry.
///
/// Two-pass deserialise (envelope first, then payload by type) avoids the
/// polymorphic-base-class dance that AOT source-gen handles awkwardly. Each
/// domain owns its own payload types under <c>Helper/Domains/</c>.
/// </summary>
public sealed class HelperEnvelope
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// RPC reply for a command that carried an <c>Id</c>. Receivers correlate by id.
/// </summary>
public sealed class HelperResult
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Payload { get; set; }

    internal static HelperResult OkFor(string? id) => new() { Id = id ?? "", Ok = true };
    internal static HelperResult Fail(string? id, string error) => new() { Id = id ?? "", Ok = false, Error = error };
}

/// <summary>
/// First message the helper sends after connecting. Lets the service know
/// which session the helper claims (cross-checked against the pipe client
/// process id) and the helper's pid for diagnostics. Lives on the transport
/// side because the connection handshake is part of the transport contract,
/// not any one domain.
/// </summary>
public sealed class HelperHello
{
    public int SessionId { get; set; }
    public int Pid { get; set; }
    public string Version { get; set; } = "";
}
