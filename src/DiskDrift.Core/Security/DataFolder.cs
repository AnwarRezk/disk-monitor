using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskDrift.Core.Security;

/// <summary>
/// The Collector's data folder (<c>%ProgramData%\DiskDrift</c>): the DB and logs live here.
/// SYSTEM and Administrators get full control, Users read-only (ADR 0002), so no unprivileged
/// user can rewrite history.
/// </summary>
public static class DataFolder
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskDrift");

    /// <summary>
    /// Creates the folder with the locked-down ACL if it does not exist yet. An existing folder is left
    /// untouched. Returns true when this call created it.
    /// </summary>
    public static bool EnsureCreated(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        if (dir.Exists)
            return false;

        dir.Parent?.Create();
        dir.Create(CreateSecurity());
        return true;
    }

    /// <summary>
    /// A protected DACL (nothing inherited from ProgramData, whose defaults let Users create files):
    /// SYSTEM and Administrators full control, Users read and execute, all inherited by children.
    /// </summary>
    public static DirectorySecurity CreateSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, inherit));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, inherit));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit));
        return security;
    }

    private static FileSystemAccessRule Allow(WellKnownSidType sid, FileSystemRights rights, InheritanceFlags inherit) =>
        new(new SecurityIdentifier(sid, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow);
}
