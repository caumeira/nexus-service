using System;

namespace Nexus.Service.Cloud;

/// <summary>
/// One definition of where our cloud API lives. Shared by
/// <see cref="CloudApiClient"/> and by the widget proxy, which stamps the
/// build credential only when a widget's request targets this host.
/// </summary>
internal static class CloudApiEndpoint
{
    public const string DefaultBaseUrl = "https://api.hellonexus.com";

    /// <summary>Base URL with any trailing slash trimmed, honoring the environment override.</summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("NEXUS_API_BASE")?.TrimEnd('/') ?? DefaultBaseUrl;

    /// <summary>Host of <see cref="BaseUrl"/>, or empty when the override is not a parsable absolute URL.</summary>
    public static string Host => HostOf(BaseUrl);

    public static string HostOf(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) ? parsed.Host : "";
}
