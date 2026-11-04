using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskMon.Core.Security;

namespace DiskMon.Core.Tests;

public sealed class PipeAclTests
{
    [Fact]
    public void Interactive_users_may_read_and_write_but_not_create_instances()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Pipe ACLs are Windows-only.");

        var security = PipeAcl.Create();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToDictionary(r => ((SecurityIdentifier)r.IdentityReference).Value);

        Assert.Equal(3, rules.Count);
        Assert.All(rules.Values, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(PipeAccessRights.FullControl, rules[Sid(WellKnownSidType.LocalSystemSid)].PipeAccessRights);
        Assert.Equal(PipeAccessRights.FullControl, rules[Sid(WellKnownSidType.BuiltinAdministratorsSid)].PipeAccessRights);

        var interactive = rules[Sid(WellKnownSidType.InteractiveSid)].PipeAccessRights;
        Assert.Equal(PipeAccessRights.ReadWrite, interactive & PipeAccessRights.ReadWrite);
        Assert.Equal((PipeAccessRights)0, interactive & PipeAccessRights.CreateNewInstance);
    }

    private static string Sid(WellKnownSidType type) => new SecurityIdentifier(type, null).Value;
}
