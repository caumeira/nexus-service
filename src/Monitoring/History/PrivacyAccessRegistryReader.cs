using System.Collections.Generic;
#if WINDOWS
using System;
using Microsoft.Win32;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Monitoring.History;

/// <summary>One ConsentStore per-app entry: FILETIME fields are raw registry
/// values (0 for LastUsedTimeStop means the capability is in use right now),
/// unconverted so FileTimeConversion stays the single conversion point.</summary>
public sealed record PrivacyAccessRawEntry(string Capability, string AppId, long StartFileTime, long StopFileTime);

/// <summary>Snapshot of every tracked capability's per-app entries. The only
/// production implementation reads the Windows registry; tests substitute a
/// stub so PrivacyAccessWatcherTests can drive transitions without a real
/// console user or registry.</summary>
public interface IPrivacyAccessRegistryReader
{
    IReadOnlyList<PrivacyAccessRawEntry> ReadAll();
}

#if WINDOWS
/// <summary>
/// Reads HKEY_USERS\&lt;console-user-sid&gt;\...\CapabilityAccessManager\ConsentStore.
/// Packaged apps are direct subkeys of a capability (name = package family
/// name); Win32 apps live under &lt;capability&gt;\NonPackaged\&lt;encoded-path&gt;,
/// where the encoded path has '\' replaced with '#'. Returns empty when no
/// console user is logged on or the hive isn't loaded yet.
/// </summary>
public sealed class PrivacyAccessRegistryReader : IPrivacyAccessRegistryReader
{
    private const string ConsentStoreSubPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackagedSubkeyName = "NonPackaged";

    public IReadOnlyList<PrivacyAccessRawEntry> ReadAll()
    {
        var sid = ConsoleUserSid.Resolve();
        if (sid is null)
        {
            return Array.Empty<PrivacyAccessRawEntry>();
        }

        using var consentStore = Registry.Users.OpenSubKey($@"{sid}\{ConsentStoreSubPath}");
        if (consentStore is null)
        {
            return Array.Empty<PrivacyAccessRawEntry>();
        }

        var result = new List<PrivacyAccessRawEntry>();
        foreach (var capability in PrivacyAccess.TrackedCapabilities)
        {
            using var capKey = consentStore.OpenSubKey(capability);
            if (capKey is null)
            {
                continue;
            }

            foreach (var subkeyName in capKey.GetSubKeyNames())
            {
                if (string.Equals(subkeyName, NonPackagedSubkeyName, StringComparison.OrdinalIgnoreCase))
                {
                    ReadNonPackaged(capKey, capability, result);
                    continue;
                }

                using var packagedKey = capKey.OpenSubKey(subkeyName);
                if (TryReadEntry(packagedKey, capability, subkeyName, out var entry))
                {
                    result.Add(entry);
                }
            }
        }
        return result;
    }

    private static void ReadNonPackaged(RegistryKey capKey, string capability, List<PrivacyAccessRawEntry> result)
    {
        using var nonPackaged = capKey.OpenSubKey(NonPackagedSubkeyName);
        if (nonPackaged is null)
        {
            return;
        }
        foreach (var encodedPath in nonPackaged.GetSubKeyNames())
        {
            using var appKey = nonPackaged.OpenSubKey(encodedPath);
            if (TryReadEntry(appKey, capability, DecodeExePath(encodedPath), out var entry))
            {
                result.Add(entry);
            }
        }
    }

    internal static string DecodeExePath(string encoded) => encoded.Replace('#', '\\');

    private static bool TryReadEntry(RegistryKey? key, string capability, string appId, out PrivacyAccessRawEntry entry)
    {
        entry = default!;
        if (key?.GetValue("LastUsedTimeStart") is not long start)
        {
            return false;
        }
        var stop = key.GetValue("LastUsedTimeStop") is long s ? s : 0L;
        entry = new PrivacyAccessRawEntry(capability, appId, start, stop);
        return true;
    }
}
#else
public sealed class PrivacyAccessRegistryReader : IPrivacyAccessRegistryReader
{
    public IReadOnlyList<PrivacyAccessRawEntry> ReadAll() => System.Array.Empty<PrivacyAccessRawEntry>();
}
#endif
