using DiskMon.Core.Config;
using DiskMon.Core.Probes;

namespace DiskMon.Core.Tests;

/// <summary>ProbeExpander against a fake <c>C:\Users</c> tree in a temp folder.</summary>
public sealed class ProbeExpanderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _users;

    public ProbeExpanderTests()
    {
        _users = Path.Combine(_temp.Path, "Users");
        foreach (var profile in new[] { "alice", "Bob", "Default", "Default User", "Public", "All Users", "default" })
            Directory.CreateDirectory(Path.Combine(_users, profile));
        File.WriteAllText(Path.Combine(_users, "desktop.ini"), "not a profile");
    }

    public void Dispose() => _temp.Dispose();

    private string User(string name, params string[] rest) => Path.Combine([_users, name, .. rest]);

    [Fact]
    public void User_template_expands_over_real_profiles_only()
    {
        var expander = new ProbeExpander(_users);

        Assert.Equal([User("alice"), User("Bob")], expander.UserProfiles());

        var probes = expander.Expand([new ProbeDefinition { Name = "User Temp", Path = @"{user}\AppData\Local\Temp" }]);

        Assert.Equal(
            [
                new ExpandedProbe("User Temp (alice)", ProbeKind.Dir, User("alice", "AppData", "Local", "Temp"), ProbeExpander.VolumeOf(_users)),
                new ExpandedProbe("User Temp (Bob)", ProbeKind.Dir, User("Bob", "AppData", "Local", "Temp"), ProbeExpander.VolumeOf(_users)),
            ],
            probes);
    }

    [Fact]
    public void Fixed_and_vss_probes_pass_through_with_their_volume()
    {
        var probes = new ProbeExpander(_users).Expand(
        [
            new ProbeDefinition { Name = "Windows Temp", Path = @"C:\Windows\Temp\" },
            new ProbeDefinition { Name = "Page file", Kind = ProbeKind.File, Path = @"c:\pagefile.sys" },
            new ProbeDefinition { Name = "VSS", Kind = ProbeKind.Vss, Path = "c:" },
            new ProbeDefinition { Name = "Duplicate", Path = @"C:\WINDOWS\Temp" },
        ]);

        Assert.Equal(3, probes.Count);
        Assert.Equal("C:", probes[0].Volume);
        Assert.Equal(ProbeExpander.NormalizeSeparators(@"C:\Windows\Temp"), probes[0].Path);
        Assert.Equal("dir", probes[0].KindText);
        Assert.Equal("C:", probes[1].Volume);
        Assert.Equal(new ExpandedProbe("VSS", ProbeKind.Vss, "C:", "C:"), probes[2]);
    }

    [Fact]
    public void No_users_folder_means_no_per_user_probes()
    {
        var expander = new ProbeExpander(Path.Combine(_temp.Path, "missing"));
        Assert.Empty(expander.Expand([new ProbeDefinition { Name = "Temp", Path = @"{user}\Temp" }]));
    }

    [Fact]
    public void Globs_expand_per_user_and_match_case_insensitively_at_run_time()
    {
        Directory.CreateDirectory(User("alice", "AppData", "Local", "Packages", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc", "LocalState"));
        Directory.CreateDirectory(User("alice", "AppData", "Local", "Packages", "canonicalgroup.Debian_x", "LocalState"));
        Directory.CreateDirectory(User("alice", "AppData", "Local", "Packages", "Microsoft.Other", "LocalState"));
        Directory.CreateDirectory(User("alice", "AppData", "Local", "Packages", "CanonicalGroupNoState"));

        var probe = Assert.Single(new ProbeExpander(_users).Expand(
            [new ProbeDefinition { Name = "WSL disks", Kind = ProbeKind.Glob, Path = @"{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState" }]),
            p => p.Name == "WSL disks (alice)");

        Assert.Equal(ProbeKind.Glob, probe.Kind);
        Assert.Equal(
            [
                User("alice", "AppData", "Local", "Packages", "canonicalgroup.Debian_x", "LocalState"),
                User("alice", "AppData", "Local", "Packages", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc", "LocalState"),
            ],
            ProbeExpander.ResolveGlob(probe.Path));
    }

    [Fact]
    public void Globs_match_files_in_the_last_segment()
    {
        File.WriteAllText(User("alice", "a.vhdx"), "x");
        File.WriteAllText(User("alice", "b.txt"), "x");

        Assert.Equal([User("alice", "a.vhdx")], ProbeExpander.ResolveGlob(User("alice", "*.vhdx")));
        Assert.Empty(ProbeExpander.ResolveGlob(User("nobody", "*.vhdx")));
    }

    [Fact]
    public void The_default_config_expands_every_per_user_probe_for_each_profile()
    {
        var defaults = OptionsTests.BindShippedAppSettings().Probes;
        var probes = new ProbeExpander(_users).Expand(defaults);

        var perUser = defaults.Count(d => d.IsPerUser);
        Assert.Equal(defaults.Count - perUser + 2 * perUser, probes.Count);
        Assert.DoesNotContain(probes, p => p.Path.Contains("{user}", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(probes, p => p.Name.Contains("(Public)") || p.Name.Contains("(Default"));
    }
}
