using System;
using System.Net;

namespace Nexus.Service.QSeries;

/// <summary>
/// One persisted Q-series TCP transport. Captured once when a Q-series
/// device is first promoted from USB-FFS adb to TCP-mode adb, then used on
/// subsequent service starts to bring the transport back up via
/// <c>adb connect</c> without waiting for USB re-enumeration.
/// </summary>
/// <param name="Model">
/// Device model string from <c>getprop ro.product.model</c> at promotion
/// time. Diagnostic / log breadcrumb only — the (serial, ip, port) tuple
/// is what the watcher acts on.
/// </param>
/// <param name="IpAddress">
/// Device's LAN IPv4 address discovered during promotion. The watcher's
/// connect retries this address verbatim; if the device has moved to a
/// new DHCP lease the connect will fail and the record gets evicted on
/// the next USB re-promotion.
/// </param>
/// <param name="Port">
/// Listening port on the device's adbd after <c>adb tcpip &lt;port&gt;</c>.
/// Always 5555 for now — kept as a field so a future "use a non-default
/// port to avoid colliding with another adb-over-WiFi host" knob doesn't
/// invalidate the on-disk format.
/// </param>
/// <param name="PromotedAt">
/// When the promotion ran. Lets us age out very old records on startup
/// if we ever need to (not done today; kept as observability metadata).
/// </param>
public sealed record QSeriesTransportRecord(
    string Model,
    string IpAddress,
    int Port,
    DateTimeOffset PromotedAt);

/// <summary>
/// Pure helpers used by the transport-promotion path. Kept separate from
/// <see cref="QSeriesPortWatcher"/> so the parsing + private-IP gating
/// can be unit-tested without standing up an adb-server.
/// </summary>
public static class QSeriesTransport
{
    /// <summary>
    /// Default port adbd listens on after <c>adb tcpip</c>. Matches
    /// Android's documented default — overriding it requires writing
    /// <c>service.adb.tcp.port</c> via setprop, which we don't do.
    /// </summary>
    public const int DefaultAdbTcpPort = 5555;

    /// <summary>
    /// True if the adb device serial looks like a TCP transport
    /// (<c>192.168.1.42:5555</c>) vs. a USB serial (<c>0123456789ABCDEF</c>).
    /// adb's own convention: TCP serials always contain a colon between
    /// host and port.
    /// </summary>
    public static bool IsTcpSerial(string serial) =>
        !string.IsNullOrEmpty(serial) && serial.Contains(':');

    /// <summary>
    /// Extracts the first private-range LAN IPv4 from an adb shell output.
    /// We try several commands at the call site (<c>ip route get 1.1.1.1</c>,
    /// <c>ip -4 addr show wlan0</c>, <c>getprop dhcp.wlan0.ipaddress</c>)
    /// and feed each output through this. Restricted to RFC 1918 + CGNAT
    /// ranges because: (a) Q-series devices live on home/office LANs in
    /// every realistic deployment, and (b) the marker IP that <c>ip route
    /// get</c> echoes back (we use <c>1.1.1.1</c>) would otherwise be
    /// returned ahead of the device's actual src address. Returns null if
    /// nothing in the output looks like a private LAN IPv4 — caller falls
    /// back to the next discovery command, or gives up and defers to the
    /// next watcher tick.
    /// </summary>
    public static IPAddress? ParseLanIPv4(string? shellOutput)
    {
        if (string.IsNullOrWhiteSpace(shellOutput)) return null;
        // Whitespace + common shell-output separators. Keeps the parser
        // free of regex (AOT-friendly) and tolerant of `ip route` /
        // `ip addr` quirks across Android versions.
        var separators = new[] { ' ', '\t', '\r', '\n', ',', ';', '/', '(', ')', '"', '\'' };
        var tokens = shellOutput.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (!IPAddress.TryParse(token, out var addr)) continue;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            if (!IsPrivateLanIPv4(addr)) continue;
            return addr;
        }
        return null;
    }

    /// <summary>
    /// IPv4 addresses we're willing to call <c>adb connect</c> on. Accepts
    /// RFC 1918 private ranges (10/8, 172.16/12, 192.168/16) and CGNAT
    /// (100.64/10). Rejects loopback, link-local, public IPs, and anything
    /// outside those ranges. The intentional narrowness keeps the parser
    /// from grabbing the destination marker IP (<c>1.1.1.1</c>) that
    /// <c>ip route get</c> echoes back before the device's actual src.
    /// </summary>
    public static bool IsPrivateLanIPv4(IPAddress addr)
    {
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = addr.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 100.64.0.0/10 — CGNAT (some ISPs use this for residential WAN,
        // but it can also legitimately appear on internal LANs).
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }
}
