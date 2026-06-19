using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Update;

/// <summary>
/// Provider seam for the update manifest. Implementations are channel-aware;
/// callers pass "production" or "beta" and receive a normalized manifest or null
/// (no update available / provider unreachable).
/// </summary>
public interface IUpdateSource
{
    Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct);
}
