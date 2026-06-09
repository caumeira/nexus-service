using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Plugins.Transport;

/// <summary>
/// The OS-verified identity of the process at the other end of a transport. Pid
/// and uid come from the kernel (Linux <c>SO_PEERCRED</c> / macOS
/// <c>LOCAL_PEERCRED</c>+<c>LOCAL_PEERPID</c>), never from anything the peer
/// claims — this is the spine of the broker's creation-time peer pin: the host
/// pins the child pid at spawn, then rejects any connection whose kernel-attested
/// pid doesn't match.
/// </summary>
public readonly record struct PeerCredentials(int ProcessId, int UserId);

/// <summary>
/// A connected duplex byte channel to one plugin process. The length-prefix
/// <c>Framing</c> rides on <see cref="Stream"/>; <see cref="Peer"/> is the
/// kernel-attested identity of the far end. One standardized shape on every OS
/// (named pipe on Windows, Unix domain socket on mac/Linux) so the broker and
/// the language SDKs see the same contract.
/// </summary>
public interface IDuplexTransport : IDisposable
{
    Stream Stream { get; }
    PeerCredentials Peer { get; }
}

/// <summary>
/// A host-owned endpoint that accepts plugin connections. The host is always the
/// server (no inversion); every accepted connection carries verified peer creds.
/// </summary>
public interface IDuplexListener : IDisposable
{
    /// <summary>The address plugins connect to (pipe name / socket path).</summary>
    string Endpoint { get; }

    Task<IDuplexTransport> AcceptAsync(CancellationToken ct);
}
