#if WINDOWS
using System;
using System.IO;
using Microsoft.Win32;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Resolves the active console user's SID, so callers can read that user's
/// hive under HKEY_USERS from the LocalSystem service, whose own HKCU is the
/// SYSTEM hive rather than the interactive user's.
/// </summary>
internal static class ConsoleUserSid
{
    private const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static string? Resolve()
    {
        var username = UserHelperBootstrapper.ResolveActiveConsoleUsername();
        return string.IsNullOrEmpty(username) ? null : ResolveForUsername(username);
    }

    // ProfileList maps a profile folder name (e.g. "C:\Users\Nicola") to the
    // SID that owns it; there is no direct username-to-SID registry value.
    internal static string? ResolveForUsername(string username)
    {
        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListPath);
        if (profileList is null)
        {
            return null;
        }

        foreach (var sid in profileList.GetSubKeyNames())
        {
            using var profile = profileList.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is not string imagePath)
            {
                continue;
            }
            var profileUsername = Path.GetFileName(imagePath.TrimEnd('\\'));
            if (string.Equals(profileUsername, username, StringComparison.OrdinalIgnoreCase))
            {
                return sid;
            }
        }
        return null;
    }
}
#endif
