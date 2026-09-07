using System.Net.Http;

namespace Nexus.Service.Common;

/// <summary>Ed25519-signed build credential every call to our backend carries; empty in a build we did not publish, which is what makes that build offline. Extractable from any shipped binary, so it gates rather than authorizes - see plans/open-source-cloud-boundary.md.</summary>
internal static class ClientCredential
{
    public const string HeaderName = "X-Nexus-Client";

    /// <summary>Signed build token, or empty in an unofficial build.</summary>
    public static string Token => BuildInfo.ClientToken;

#if OFFICIAL_BUILD
    public static bool IsOfficial => !string.IsNullOrEmpty(BuildInfo.ClientToken);
#else
    // Constant false, so the guarded egress paths are unreachable and trimmable.
    public static bool IsOfficial => false;
#endif

    /// <summary>Stamps the credential; sends no header at all when unofficial.</summary>
    public static void Apply(HttpClient client) => Apply(client, Token);

    /// <summary>Stamps one request, for a client shared with hosts that are not ours.</summary>
    public static void Apply(HttpRequestMessage request)
    {
        if (IsOfficial)
        {
            Apply(request, Token);
        }
    }

    // The baked value is a compile-time const, so this overload is the only way a test reaches the official path.
    internal static void Apply(HttpClient client, string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, token);
        }
    }

    // Same reason as the HttpClient overload: the only way a test reaches the official path.
    internal static void Apply(HttpRequestMessage request, string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation(HeaderName, token);
        }
    }
}
