using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart;

internal static class LanHost
{
    /// <summary>Resolve a user-supplied host to an IPv4 literal, or null. The
    /// streaming paths send raw UDP datagrams and must not block on DNS per
    /// frame, so drivers resolve once at pair time and persist the IP.</summary>
    public static async Task<string?> ResolveIpv4Async(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        // Normalize the literal: reply matching keys on the canonical dotted
        // form, so "127.1"-style shorthand must not survive as-is.
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : null;
        try
        {
            foreach (var a in await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a.ToString();
        }
        catch (System.OperationCanceledException) { throw; }
        catch { /* unresolvable */ }
        return null;
    }
}
