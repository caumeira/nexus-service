#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Migration;

#if WINDOWS
/// <summary>
/// Candidate user profile directories a Nexus 2 install may live under, for
/// the LocalSystem service to scan (it cannot see the console user's own
/// %APPDATA% directly). Shared by <see cref="Nexus2Detector"/> and
/// <see cref="Nexus2ConfigReader"/> so both walk the same resolution order.
/// </summary>
internal static class Nexus2ProfileDirs
{
    /// <summary>Console user's profile first (via <see cref="ConsoleUserSid"/>), then every
    /// other real profile under &lt;systemdrive&gt;\Users, newest data first is left to the
    /// caller (this only orders console-user-first).</summary>
    public static IEnumerable<string> Candidates()
    {
        string? consoleProfile = null;
        try
        {
            consoleProfile = ConsoleUserSid.ResolveProfilePath();
        }
        catch { /* fall through to scan */ }

        if (!string.IsNullOrEmpty(consoleProfile))
        {
            yield return consoleProfile;
        }

        string? usersRoot = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(root))
            {
                usersRoot = Path.Combine(root, "Users");
            }
        }
        catch { /* ignore */ }
        if (usersRoot is null || !SafeDirExists(usersRoot))
        {
            yield break;
        }

        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(usersRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var profile in profiles)
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users")
            {
                continue;
            }
            if (string.Equals(profile, consoleProfile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return profile;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
#endif
