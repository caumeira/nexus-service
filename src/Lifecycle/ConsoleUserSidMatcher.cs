using System;
using System.Collections.Generic;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Pure matching for ConsoleUserSid: given ProfileList's (sid, profile
/// folder name) pairs, picks the SID whose folder is the console username.
/// An exact match wins outright; otherwise a folder suffixed with
/// "username." (domain-joined or collision-suffixed profiles, e.g.
/// "user.DOMAIN", "user.000") is accepted only when exactly one such folder
/// exists - more than one is ambiguous and zero is not a match, so both
/// return null rather than guessing.
/// </summary>
internal static class ConsoleUserSidMatcher
{
    public static string? Match(IEnumerable<(string Sid, string ProfileUsername)> profiles, string username)
    {
        var prefix = username + ".";
        List<string>? prefixMatches = null;

        foreach (var (sid, profileUsername) in profiles)
        {
            if (string.Equals(profileUsername, username, StringComparison.OrdinalIgnoreCase))
            {
                return sid;
            }
            if (profileUsername.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                (prefixMatches ??= new List<string>()).Add(sid);
            }
        }

        return prefixMatches is { Count: 1 } ? prefixMatches[0] : null;
    }
}
