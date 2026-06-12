using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Relay;
using Nexus.Service.Serialization;

namespace Nexus.Service.Webcam;

/// <summary>
/// LAN-only gate for the webcam surface. The webcam paths are deliberately
/// absent from <see cref="RelayHttpAllowlist"/>, so relayed requests are
/// already blocked at the dispatcher; this guard is the explicit in-module
/// backstop so the contract survives a future allowlist edit. Presence of the
/// trusted-dispatch item is enough to refuse: only
/// <see cref="RelayHttpDispatcher"/> populates that key, and a network caller
/// can never set HttpContext.Items.
/// </summary>
internal static class WebcamRelayGuard
{
    public static bool IsRelayDispatch(HttpContext ctx) =>
        ctx.Items.ContainsKey(RelayHttpDispatcher.TrustedRelayDispatchKey);

    /// <summary>Forbidden result when the request rode the relay tunnel; null when direct.</summary>
    public static IResult? Refuse(HttpContext ctx) => IsRelayDispatch(ctx)
        ? Results.Json(
            ApiResponse.Fail("Webcam is available on the local network only"),
            AppJsonContext.Default.ApiResponse,
            statusCode: StatusCodes.Status403Forbidden)
        : null;
}
