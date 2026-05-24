using Microsoft.AspNetCore.Builder;

namespace Nexus.Service.Auth;

/// <summary>
/// Endpoint metadata marker for routes that may only be invoked from the
/// loopback interface (127.0.0.1 / ::1). Used for service-control surface
/// (<c>/service/stop</c>, <c>/service/startup-mode</c>) so a malicious LAN
/// device with a leaked dashboard token still cannot stop or reconfigure
/// the service.
///
/// Checked in the auth middleware <em>before</em> token validation so a
/// non-loopback caller can never even probe the route's existence - it
/// gets the same 404 they would for any unmapped path.
/// </summary>
public sealed class LocalhostOnlyAccess
{
    public static readonly LocalhostOnlyAccess Instance = new();
    private LocalhostOnlyAccess() { }
}

public static class LocalhostOnlyEndpointExtensions
{
    /// <summary>
    /// Restrict the endpoint to loopback callers. Attach per-endpoint at
    /// the registration site (chained on MapPost/MapGet/etc.).
    /// </summary>
    public static TBuilder LocalhostOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(LocalhostOnlyAccess.Instance);
        return builder;
    }
}
