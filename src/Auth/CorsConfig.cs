using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace Nexus.Service.Auth;

/// <summary>
/// CORS origin allowlist for the local service. The only trusted browser
/// origins are the bundled SPA on loopback and the hosted web app at
/// hellonexus.com, which fetches the per-installation token from /pair (itself
/// loopback-only) and then drives the service from the browser.
///
/// Matching is EXACT full-origin string equality (via <c>WithOrigins</c>) —
/// never substring / prefix / suffix — so a look-alike host such as
/// <c>https://hellonexus.com.attacker.com</c> is rejected. This is the exact
/// class of bug behind the ASUS DriverHub RCE (CVE-2025-3462/3463), where a
/// substring origin check let <c>driverhub.asus.com.attacker.com</c> through.
/// Do not replace <see cref="AddNexusCors"/>'s <c>WithOrigins</c> with a
/// <c>SetIsOriginAllowed</c> substring/Contains/EndsWith predicate — see
/// CorsConfigTests for the regression guard.
/// </summary>
public static class CorsConfig
{
    public static string[] BuildAllowedOrigins(int httpPort, int httpsPort)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://localhost:{httpPort}",
            $"http://127.0.0.1:{httpPort}",
            "https://hellonexus.com",
            "https://www.hellonexus.com",
        };

        if (httpsPort > 0)
        {
            origins.Add($"https://localhost:{httpsPort}");
            origins.Add($"https://127.0.0.1:{httpsPort}");
        }

        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
        {
            origins.Add($"http://{Environment.MachineName}:{httpPort}");
            if (httpsPort > 0)
                origins.Add($"https://{Environment.MachineName}:{httpsPort}");
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip) &&
                        !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        origins.Add($"http://{ip}:{httpPort}");
                        if (httpsPort > 0)
                            origins.Add($"https://{ip}:{httpsPort}");
                    }
                }
            }
        }
        catch { }

        return origins.ToArray();
    }

    /// <summary>
    /// Registers the default CORS policy.
    /// <paramref name="debugLoopbackWildcard"/> (DEBUG builds only) accepts any
    /// <c>http://localhost:*</c> / <c>http://127.0.0.1:*</c> so the Vite dev
    /// server on a random loopback port can reach the service. In release it is
    /// false and the policy is the strict exact-match <paramref name="allowedOrigins"/>
    /// allowlist. Even the loose dev predicate requires the <c>:</c> port
    /// separator, so <c>http://localhost.attacker.com</c> does not match.
    /// </summary>
    public static IServiceCollection AddNexusCors(
        this IServiceCollection services, string[] allowedOrigins, bool debugLoopbackWildcard)
    {
        return services.AddCors(c => c.AddDefaultPolicy(p =>
        {
            if (debugLoopbackWildcard)
            {
                p.SetIsOriginAllowed(origin =>
                    origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                    origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                p.WithOrigins(allowedOrigins);
            }

            p.AllowAnyMethod().AllowAnyHeader();
        }));
    }
}
