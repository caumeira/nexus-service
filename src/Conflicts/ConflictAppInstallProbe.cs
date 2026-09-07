using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Whether a catalog app is installed, as opposed to running. The Nexus
/// Control default for a shared-bus device keys off this: the service starts
/// in session 0 before the vendor app launches at logon, so a running-process
/// check at boot would say "absent" on exactly the boxes that need the yield.
/// </summary>
public interface IConflictAppInstallProbe
{
    bool IsInstalled(string appId);
}

/// <summary>
/// Installed = the Service Control Manager lists a service the app owns.
/// Answered once per app per process: install state does not change under a
/// running service, and a restart re-evaluates. False on any failure and
/// everywhere but Windows, so a probe that cannot answer leaves Nexus Control
/// at its normal default rather than silently disabling a device.
/// </summary>
public sealed class ConflictAppInstallProbe : IConflictAppInstallProbe
{
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsInstalled(string appId) => _cache.GetOrAdd(appId, Probe);

    private static bool Probe(string appId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        var def = ConflictWatcher.FindById(appId);
        if (def is null)
        {
            return false;
        }
        // A catalog entry names services by key and processes by executable
        // basename, and which of the two a vendor used is not knowable here:
        // iCUE's control service is registered as CorsairDeviceControlService
        // and displayed as "Corsair Device Control Service". Match both fields
        // of every installed service against both lists.
        var wanted = new List<string>(def.WindowsServiceNames.Length + def.ProcessNames.Length);
        wanted.AddRange(def.WindowsServiceNames);
        wanted.AddRange(def.ProcessNames);
        if (wanted.Count == 0)
        {
            return false;
        }
        foreach (var svc in WindowsServiceController.ListServices())
        {
            if (Matches(wanted, svc.Key) || Matches(wanted, svc.DisplayName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a service name matches any wanted name once both are reduced
    /// to their letters and digits, so "Corsair Device Control Service" matches
    /// the catalog's "CorsairDeviceControlService". Exposed for tests.
    /// </summary>
    internal static bool Matches(IReadOnlyList<string> wanted, string serviceName)
    {
        var normalized = Normalize(serviceName);
        if (normalized.Length == 0)
        {
            return false;
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (string.Equals(normalized, Normalize(wanted[i]), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }
        return new string(buffer[..length]);
    }
}
