using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.Service.Platform;

/// <summary>
/// Locks a %ProgramData% directory so a non-admin local user can't plant a file
/// the LocalSystem daemon later trusts or executes. %ProgramData% grants Users
/// create-file/create-subdir by inheritance, so any dir the service reads an
/// install directive or executable from must replace that inherited ACL.
/// </summary>
public static class WindowsDirectorySecurity
{
    /// <summary>
    /// True when the current process is the LocalSystem account - the daemon
    /// whose %ProgramData% dirs need locking. Windows-only; call under an
    /// OperatingSystem.IsWindows() guard.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsLocalSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }

    /// <summary>
    /// Creates <paramref name="dir"/> if absent and applies a protected ACL:
    /// SYSTEM + Administrators FullControl, Users ReadAndExecute, inheritance
    /// from the parent dropped. When <paramref name="resetOwner"/> is true, also
    /// resets the owner to Administrators - a dir a non-admin pre-created is
    /// owned by that user, who then retains WRITE_DAC and could rewrite the ACL
    /// back. Owner reset needs SeRestorePrivilege and is best-effort; the DACL is
    /// always applied. Windows-only; call under an OperatingSystem.IsWindows() guard.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Protect(string dir, bool resetOwner = false)
    {
        var info = Directory.CreateDirectory(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(WellKnownSidType sid, FileSystemRights rights) =>
            sec.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        info.SetAccessControl(sec);

        if (resetOwner)
        {
            try
            {
                var owner = new DirectorySecurity();
                owner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                info.SetAccessControl(owner);
            }
            catch { }
        }
    }
}
