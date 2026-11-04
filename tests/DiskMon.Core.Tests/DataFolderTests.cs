using System.Security.AccessControl;
using System.Security.Principal;
using DiskMon.Core.Security;

namespace DiskMon.Core.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_existing_folder_is_left_alone()
    {
        Assert.False(DataFolder.EnsureCreated(_temp.Path));
    }

    [Fact]
    public void New_folder_gives_SYSTEM_and_Administrators_full_control_and_Users_read_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Folder ACLs are Windows-only.");
        var path = _temp.File("DiskMon");

        Assert.True(DataFolder.EnsureCreated(path));

        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "The DACL must not inherit ProgramData's defaults.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(3, rules.Count);
        AssertRights(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AssertRights(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AssertRights(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    }

    [Fact]
    public void Files_created_inside_inherit_Users_read_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Folder ACLs are Windows-only.");
        // Creating a file in the locked folder needs Administrators write access, which is the point
        // of the ACL. CI runners are elevated; a normal developer shell skips this one.
        Assert.SkipUnless(IsElevated(), "Needs an elevated process to write into the locked folder.");
        var path = _temp.File("DiskMon");
        DataFolder.EnsureCreated(path);
        var file = Path.Combine(path, "diskmon.db");
        File.WriteAllText(file, "");

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var userRights = new FileInfo(file).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference.Equals(users))
            .Aggregate(default(FileSystemRights), (acc, r) => acc | r.FileSystemRights);

        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, userRights);
        Assert.Equal(default, userRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete));
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void AssertRights(List<FileSystemAccessRule> rules, WellKnownSidType sid, FileSystemRights expected)
    {
        var identity = new SecurityIdentifier(sid, null);
        var rule = Assert.Single(rules, r => r.IdentityReference.Equals(identity));
        Assert.Equal(expected, rule.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
    }
}
