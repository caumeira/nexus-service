using System.Collections.Generic;
using System.Text.Json;

namespace Qos.Service.Helper;

/// <summary>
/// Wire envelope for every message on the helper pipe in either direction.
/// `Type` is the discriminator (`<domain>.<verb>`, e.g. `trayIcon.setVisible`,
/// `screenTime.sessionEnded`). `Id` is the correlation id for RPC-style
/// commands; null for one-way telemetry. `Payload` is the raw JSON, decoded
/// by the receiver using the appropriate `AppJsonContext` entry.
///
/// Two-pass deserialise (envelope first, then payload by type) avoids the
/// polymorphic-base-class dance that AOT source-gen handles awkwardly, and
/// makes adding a new command type a 2-line change: add the payload DTO +
/// register a handler. No edits to envelope code.
/// </summary>
public sealed class HelperEnvelope
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// RPC reply for a command that carried an `Id`. Receivers correlate by id.
/// </summary>
public sealed class HelperResult
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// First message the helper sends after connecting. Lets the service know
/// which session the helper claims (cross-checked against the pipe client
/// process id) and the helper's pid for diagnostics.
/// </summary>
public sealed class HelperHello
{
    public int SessionId { get; set; }
    public int Pid { get; set; }
    public string Version { get; set; } = "";
}

/// <summary>
/// Payload for `trayIcon.setVisible`. Service-to-helper. No reply expected
/// beyond the standard ok-result.
/// </summary>
public sealed class TraySetVisiblePayload
{
    public bool Visible { get; set; }
}

/// <summary>
/// Payload for `helper.shutdown`. Service-to-helper. The service signals
/// this from its ApplicationStopping hook so the helper closes its --app
/// window (Edge --app shell) and exits, matching the settings "Stop Qos"
/// UX where stopping the service also tears down the tray and window.
/// No fields - the envelope's existence is the signal.
/// </summary>
public sealed class HelperShutdownPayload
{
}

/// <summary>
/// Payload for `service.requestStop`. Helper-to-service. Fired when the
/// user clicks "Shut down" in the tray. The service handler calls
/// IHostApplicationLifetime.StopApplication so the daemon runs the same
/// graceful shutdown the /service/stop HTTP route uses. Going over the
/// already-authenticated pipe avoids needing to widen the service's
/// SCM DACL with SERVICE_STOP for the interactive user. No fields.
/// </summary>
public sealed class ServiceRequestStopPayload
{
}

/// <summary>
/// Payload for `screenTime.session`. Helper-to-service, one-way. Emitted
/// when the foreground window changes (closing out the prior session) or
/// when the user goes idle long enough to clip the session early.
/// </summary>
public sealed class ScreenTimeSessionPayload
{
    public string App { get; set; } = "";
    public long StartedUtcMs { get; set; }
    public long EndedUtcMs { get; set; }
}

/// <summary>
/// Payload for `screenTime.focus`. Helper-to-service, one-way. Updates the
/// service's view of the currently focused app. App=="" means no focus
/// (everything minimised, lock screen, etc.).
/// </summary>
public sealed class ScreenTimeFocusPayload
{
    public string App { get; set; } = "";
    public int Pid { get; set; }
    public long StartedUtcMs { get; set; }
}

/// <summary>
/// Payload for `media.snapshot`. Helper-to-service, one-way. The full
/// current set of GSMTC media sessions (Spotify, Edge, Chrome, etc.).
/// Pushed when any session changes. The service caches the latest snapshot
/// and serves it to the SPA via GetSessions().
/// </summary>
public sealed class MediaSnapshotPayload
{
    public Dictionary<string, Qos.Service.Models.Activity.MediaSession> Sessions { get; set; } = new();
}

/// <summary>
/// Payload for `media.control`. Service-to-helper command, expects a
/// standard ok-result. Source is the friendly session key (e.g. "Spotify");
/// Action is one of play / pause / next / prev / toggle / shuffle / repeatMode.
/// </summary>
public sealed class MediaControlPayload
{
    public string Source { get; set; } = "";
    public string Action { get; set; } = "";
}

/// <summary>
/// Payload for `media.getAlbumArt`. Service-to-helper command. The helper
/// replies with an <see cref="AlbumArtResult"/> in HelperResult.Payload.
/// </summary>
public sealed class AlbumArtRequest
{
    public string Source { get; set; } = "";
}

/// <summary>
/// Payload returned in HelperResult.Payload for an `media.getAlbumArt`
/// command. Empty Bytes means no art (Stopped session, missing thumbnail).
/// </summary>
public sealed class AlbumArtResult
{
    public byte[] Bytes { get; set; } = System.Array.Empty<byte>();
}

/// <summary>
/// Display brightness request payloads. Used as input to the various
/// `displayBrightness.*` commands. Method-specific fields are tolerant of
/// being unset (the helper handler only reads the ones it needs per type).
/// </summary>
public sealed class DisplayBrightnessRequest
{
    public string Id { get; set; } = "";
    public int Percent { get; set; }
    public byte Code { get; set; }
    public int Value { get; set; }
}

/// <summary>Response wrapper for nullable int (-1 means null).</summary>
public sealed class NullableIntResult
{
    public int Value { get; set; } = -1;
    public bool HasValue { get; set; }
}

/// <summary>Response wrapper for nullable DisplayVcpDto (Ok=false means null).</summary>
public sealed class DisplayVcpResult
{
    public bool Ok { get; set; }
    public Qos.Service.Models.Displays.DisplayVcpDto? Dto { get; set; }
}

/// <summary>Response wrapper for a single boolean.</summary>
public sealed class BoolResult
{
    public bool Value { get; set; }
}

/// <summary>Response wrapper for a single string.</summary>
public sealed class StringResult
{
    public string Value { get; set; } = "";
}

/// <summary>Response wrapper for the Enumerate() list.</summary>
public sealed class DisplayListResult
{
    public List<Qos.Service.Models.Displays.DisplayDto> Displays { get; set; } = new();
}
