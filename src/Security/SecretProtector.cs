using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Security;

/// <summary>
/// Wraps DPAPI for at-rest protection of small secrets (Steam API key, Discord
/// client secret, etc.) stored in the JSON settings file. Encrypted values get a
/// <c>dpapi:</c> sentinel so the persistence layer can round-trip plain and
/// protected entries side-by-side without a schema bump. Decryption is bound to
/// the service identity (CurrentUser scope) - copying settings.json to another
/// machine or another account renders the value unreadable.
///
/// macOS / Linux: pass-through. The dev workstation does not have DPAPI; values
/// stay plain in the local config and get encrypted on first save once they
/// land on a Windows host.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;
        if (!OperatingSystem.IsWindows()) return plain;
        return ProtectWindows(plain);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return "";
        return UnprotectWindows(stored);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string plain)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            return plain;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindows(string stored)
    {
        try
        {
            var b64 = stored.Substring(Prefix.Length);
            var cipher = Convert.FromBase64String(b64);
            var bytes = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
