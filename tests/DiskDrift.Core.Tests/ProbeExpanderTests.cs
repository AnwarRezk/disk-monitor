using DiskDrift.Core.Config;
using DiskDrift.Core.Probes;

namespace DiskDrift.Core.Tests;

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
    public void User_template_expands_over_real_profiles_only_and_keeps_one_probe_row()
    {
        var expander = new ProbeExpander(_users);

        Assert.Equal([User("alice"), User("Bob")], expander.UserProfiles());

        var probes = expander.Expand([new ProbeDefinition { Name = "User Temp", Path = @"{user}\AppData\Local\Temp" }]);

        Assert.Equal(
            [
                new ExpandedProbe("User Temp", ProbeKind.Dir, ProbeExpander.NormalizeSeparators(@"{user}\AppData\Local\Temp"), ProbeExpander.VolumeOf(_users)),
            ],
            probes);
        Assert.Equal(
            [
                User("alice", "AppData", "Local", "Temp"),
                User("Bob", "AppData", "Local", "Temp"),
            ],
            expander.ExpandPaths(probes[0].Path));
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

        var expander = new ProbeExpander(_users);
        var probe = Assert.Single(expander.Expand(
            [new ProbeDefinition { Name = "WSL disks", Kind = ProbeKind.Glob, Path = @"{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState" }]));

        Assert.Equal("WSL disks", probe.Name);
        Assert.Equal(ProbeKind.Glob, probe.Kind);
        Assert.Equal(
            [
                User("alice", "AppData", "Local", "Packages", "canonicalgroup.Debian_x", "LocalState"),
                User("alice", "AppData", "Local", "Packages", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc", "LocalState"),
            ],
            expander.ExpandPaths(probe.Path).SelectMany(ProbeExpander.ResolveGlob));
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
    public void The_default_config_keeps_one_probe_per_definition_without_exposing_usernames()
    {
        var defaults = OptionsTests.BindShippedAppSettings().Probes;
        var probes = new ProbeExpander(_users).Expand(defaults);

        Assert.Equal(defaults.Count, probes.Count);
        Assert.DoesNotContain(probes, p => p.Name.Contains("(alice)", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("(Bob)", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(probes, p => p.Path.Contains("alice", StringComparison.OrdinalIgnoreCase) || p.Path.Contains("Bob", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void To_user_template_replaces_real_profile_prefixes_and_leaves_excluded_or_system_paths_alone()
    {
        Assert.Equal(
            @"{user}\AppData\Local\Programs\App.exe",
            ProbeExpander.ToUserTemplate(@"C:\Users\alice\AppData\Local\Programs\App.exe")?.Replace('/', '\\'));
        Assert.Equal(@"C:\Users\Public\Desktop\App.exe", ProbeExpander.ToUserTemplate(@"C:\Users\Public\Desktop\App.exe"));
        Assert.Equal(@"C:\Program Files\App\App.exe", ProbeExpander.ToUserTemplate(@"C:\Program Files\App\App.exe"));
        Assert.Null(ProbeExpander.ToUserTemplate(null));
    }
}
