using DiskDrift.Core.Analysis;

namespace DiskDrift.Core.Tests;

public sealed class OpenLocationTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32\fsutil.exe" };
    private static readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\", @"C:\Windows", @"C:\Windows\System32", @"C:\Users", @"C:\Users\alex", @"C:\Users\alex\AppData\Local\Temp",
        @"C:\Users\alex\AppData", @"C:\Users\alex\AppData\Local", @"C:\Users\alex\AppData\Local\Packages",
    };

    private static ExplorerTarget? Resolve(string? path) => OpenLocation.Resolve(path, Files.Contains, Dirs.Contains, @"C:\Users\alex");

    [Fact]
    public void A_folder_is_opened() =>
        Assert.Equal("\"C:\\Users\\alex\\AppData\\Local\\Temp\"", Resolve(@"C:\Users\alex\AppData\Local\Temp\")?.Arguments);

    [Fact]
    public void A_per_user_template_resolves_to_the_signed_in_profile() =>
        Assert.Equal("\"C:\\Users\\alex\\AppData\\Local\\Temp\"", Resolve(@"{user}\AppData\Local\Temp")?.Arguments);

    [Fact]
    public void A_file_such_as_a_process_exe_is_selected() =>
        Assert.Equal("/select,\"C:\\Windows\\System32\\fsutil.exe\"", Resolve(@"C:\Windows\System32\fsutil.exe")?.Arguments);

    [Fact]
    public void A_path_that_no_longer_exists_opens_its_nearest_existing_parent() =>
        Assert.Equal(new ExplorerTarget(@"C:\Users\alex", false), Resolve(@"C:\Users\alex\Old\Gone\file.bin"));

    [Fact]
    public void A_glob_falls_back_to_the_folder_above_its_wildcard() =>
        Assert.Equal(new ExplorerTarget(@"C:\Users\alex\AppData\Local\Packages", false),
            Resolve(@"{user}\AppData\Local\Packages\*CanonicalGroup*\LocalState"));

    [Fact]
    public void The_VSS_Probe_path_opens_the_drive_root()
    {
        Assert.Equal(new ExplorerTarget(@"C:\", false), Resolve("C:"));
        Assert.Equal(new ExplorerTarget(@"C:\", false), Resolve(@"C:\NoSuchFolder"));
    }

    [Fact]
    public void Nothing_to_open_without_a_path_or_an_existing_drive()
    {
        Assert.Null(Resolve(null));
        Assert.Null(Resolve(" "));
        Assert.Null(Resolve(@"Q:\Nowhere"));
    }
}
