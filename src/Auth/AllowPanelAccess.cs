using Microsoft.AspNetCore.Builder;

namespace Nexus.Service.Auth;

/// <summary>
/// Endpoint metadata marker. Routes annotated with <see cref="AllowPanelAccess"/>
/// are reachable by panel/phone session cookies (no service bearer required).
/// Routes without it remain gated to the LAN bearer token.
///
/// Route registration uses <see cref="AuthEndpointExtensions.AllowPanel{TBuilder}"/>
/// to attach this marker, so adding a new panel-reachable endpoint is a single
/// chained call at the registration site rather than a central whitelist edit.
///
/// Attach per-endpoint only. Do not call <c>.AllowPanel()</c> on a
/// <c>MapGroup(...)</c>; group metadata is inherited by every child endpoint
/// and turns a one-line edit into a silent allowlist for the whole subtree.
/// </summary>
public sealed class AllowPanelAccess
{
    public static readonly AllowPanelAccess Instance = new();

    private AllowPanelAccess() { }
}

public static class AuthEndpointExtensions
{
    public static TBuilder AllowPanel<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(AllowPanelAccess.Instance);
        return builder;
    }
}
