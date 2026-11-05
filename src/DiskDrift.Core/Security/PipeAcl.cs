using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskDrift.Core.Security;

/// <summary>
/// The <c>DiskDrift</c> pipe's ACL (ADR 0002): SYSTEM and Administrators full control, interactive users read/write so
/// an unprivileged Tray can talk to the LocalSystem Collector. Interactive users are not granted
/// <see cref="PipeAccessRights.CreateNewInstance"/>, so they cannot add instances to the Collector's pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipeAcl
{
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Allow(WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize));
        return security;
    }

    private static PipeAccessRule Allow(WellKnownSidType sid, PipeAccessRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow);
}
