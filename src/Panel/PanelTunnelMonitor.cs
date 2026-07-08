using System;
using System.Threading;

namespace Nexus.Service.Panel;

/// <summary>
/// Liveness signal for the Q-series panel's USB reverse tunnel. The service
/// binds a second loopback-only listener on <see cref="Port"/> and
/// <c>QSeriesPortWatcher</c> points the panel's <c>adb reverse</c> at it. The
/// adb tunnel is the only intended client; another loopback-local process
/// connecting there would also count, and the signal is host-global (a second
/// attached panel shares it) - parity with the single-record legacy signal.
/// On the main port, panel traffic is indistinguishable from the desktop
/// dashboard - both arrive as 127.0.0.1 - which let an open dashboard mask a
/// stranded panel from the watcher's escalation reboot.
/// <see cref="MarkInboundActivity"/> is stamped by the tunnel listener's
/// connection middleware on every inbound read, including WebSocket keepalive
/// pongs, so an idle-but-healthy panel still registers activity.
/// </summary>
public sealed class PanelTunnelMonitor
{
    private long _lastInboundActivityUnixMs;

    public PanelTunnelMonitor(int? port) => Port = port;

    /// <summary>Tunnel listener port; null when the bind was unavailable
    /// (port taken, non-Windows, test host) - consumers then fall back to the
    /// legacy record-based liveness signal.</summary>
    public int? Port { get; }

    public bool IsActive => Port is not null;

    /// <summary>Unix ms of the last inbound byte on the tunnel listener; 0 when
    /// nothing has arrived since service start.</summary>
    public long LastInboundActivityUnixMs => Interlocked.Read(ref _lastInboundActivityUnixMs);

    public void MarkInboundActivity() =>
        Interlocked.Exchange(ref _lastInboundActivityUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
