using System;
using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>
/// Pure parsing helpers for Windows <c>netstat -n -o</c> output. Lives in a
/// cross-platform file so the logic can be unit-tested on macOS even though
/// the only caller (<see cref="WindowsNetworkProvider"/>) is Windows-only.
/// </summary>
public static class NetstatParser
{
    /// <summary>
    /// Returns the set of PIDs that have at least one TCP/UDP connection to a
    /// non-loopback, non-unspecified peer. PIDs whose connections are all
    /// loopback (127.x.x.x, ::1) or listeners (0.0.0.0:0, *:*) are excluded:
    /// they do not generate any real internet/LAN traffic. This filters out
    /// pure on-box chatter (e.g. the web client and panel kiosk talking to
    /// nexus-service over localhost) so it does not pollute the network
    /// graph.
    /// </summary>
    public static HashSet<int> ParseInternetActivePids(string netstatOutput)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrEmpty(netstatOutput))
            return result;

        foreach (var line in netstatOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("UDP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // TCP: Proto Local Foreign State PID  -> 5 cols
            // UDP: Proto Local Foreign PID        -> 4 cols
            if (parts.Length < 4)
                continue;

            var foreignAddress = parts[2];
            if (!int.TryParse(parts[^1], out var pid) || pid <= 0)
                continue;
            if (IsLoopbackOrUnspecified(foreignAddress))
                continue;

            result.Add(pid);
        }

        return result;
    }

    /// <summary>
    /// True if <paramref name="hostPort"/> denotes loopback (127.x.x.x, ::1,
    /// IPv4-mapped loopback) or an unspecified/wildcard address that can
    /// never represent a real peer (0.0.0.0, ::, *).
    /// </summary>
    public static bool IsLoopbackOrUnspecified(string hostPort)
    {
        if (string.IsNullOrEmpty(hostPort))
            return true;

        // Strip port: the port follows the LAST colon. For IPv6 the address
        // is bracketed, e.g. "[::1]:443" -> last colon is the port separator.
        var lastColon = hostPort.LastIndexOf(':');
        var host = lastColon > 0 ? hostPort.Substring(0, lastColon) : hostPort;

        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host.Substring(1, host.Length - 2);

        if (host == "*" || host == "0.0.0.0" || host == "::" || host == "::1")
            return true;

        if (host.StartsWith("127.", StringComparison.Ordinal))
            return true;

        // IPv4-mapped IPv6 loopback: ::ffff:127.x.x.x
        if (host.StartsWith("::ffff:127.", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
