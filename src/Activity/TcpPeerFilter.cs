using System;

namespace Nexus.Service.Activity;

/// <summary>
/// Remote-peer classification for the Windows TCP connection table
/// (<c>GetExtendedTcpTable</c>). A peer counts as "internet" when it is not
/// loopback and not unspecified - LAN and link-local peers are real wire
/// traffic and are kept. Cross-platform file so the logic is unit-tested
/// off-Windows.
/// </summary>
public static class TcpPeerFilter
{
    /// <summary>
    /// IPv4 remote address as the raw network-order DWORD from
    /// MIB_TCPROW_OWNER_PID (first octet in the low byte on little-endian).
    /// </summary>
    public static bool IsInternetPeerV4(uint remoteAddr)
    {
        if (remoteAddr == 0)
        {
            return false; // 0.0.0.0
        }
        return (remoteAddr & 0xFF) != 127; // 127.0.0.0/8
    }

    /// <summary>
    /// IPv6 remote address as the 16-byte network-order array from
    /// MIB_TCP6ROW_OWNER_PID. IPv4-mapped addresses (::ffff:a.b.c.d) apply
    /// the IPv4 rules to their embedded address.
    /// </summary>
    public static bool IsInternetPeerV6(ReadOnlySpan<byte> remoteAddr)
    {
        if (remoteAddr.Length != 16)
        {
            return false;
        }

        var first10Zero = true;
        for (var i = 0; i < 10; i++)
        {
            if (remoteAddr[i] != 0)
            {
                first10Zero = false;
                break;
            }
        }

        // :: (unspecified) and ::1 (loopback)
        if (first10Zero && remoteAddr[10] == 0 && remoteAddr[11] == 0
            && remoteAddr[12] == 0 && remoteAddr[13] == 0 && remoteAddr[14] == 0
            && remoteAddr[15] <= 1)
        {
            return false;
        }

        // IPv4-mapped ::ffff:a.b.c.d
        if (first10Zero && remoteAddr[10] == 0xFF && remoteAddr[11] == 0xFF)
        {
            if (remoteAddr[12] == 127)
            {
                return false;
            }
            if (remoteAddr[12] == 0 && remoteAddr[13] == 0 && remoteAddr[14] == 0 && remoteAddr[15] == 0)
            {
                return false;
            }
        }

        return true;
    }
}
