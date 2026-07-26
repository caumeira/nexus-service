using System;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// SDDL edit applied to the NexusService security descriptor at install time.
/// Kept out of the Windows-only installer so the string logic runs in the
/// normal test suite on every platform.
/// </summary>
internal static class ServiceDaclSddl
{
    /// <summary>
    /// Allow Authenticated Users (AU) SERVICE_QUERY_STATUS (LC) + SERVICE_START (RP).
    /// </summary>
    internal const string AuthUsersStartAce = "(A;;LCRP;;;AU)";

    /// <summary>
    /// Returns <paramref name="sddl"/> with <see cref="AuthUsersStartAce"/> added
    /// to its DACL, or null when the ACE is already present. The ACE goes before
    /// the SACL part (S:...) when one is present, otherwise at the end. Allow
    /// ACEs belong last in a DACL, so appending preserves ordering.
    /// </summary>
    internal static string? InsertAuthUsersStartAce(string sddl)
    {
        if (sddl.Contains(AuthUsersStartAce, StringComparison.Ordinal))
        {
            return null;
        }
        var sIdx = sddl.IndexOf("S:", StringComparison.Ordinal);
        return sIdx >= 0 ? sddl.Insert(sIdx, AuthUsersStartAce) : sddl + AuthUsersStartAce;
    }
}
