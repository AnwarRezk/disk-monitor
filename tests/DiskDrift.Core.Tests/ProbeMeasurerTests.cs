using System.Diagnostics;
using DiskDrift.Core.Config;
using DiskDrift.Core.Probes;

namespace DiskDrift.Core.Tests;

public sealed class ProbeMeasurerTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private ExpandedProbe Probe(ProbeKind kind, string relative) => new("test", kind, Path.Combine(_temp.Path, relative), "");

    [Fact]
    public void A_directory_sums_every_file_below_it_including_hidden_ones()
    {
        Write(Path.Combine("dir", "a.bin"), 100);
        Write(Path.Combine("dir", "sub", "deeper", "b.bin"), 250);
        var hidden = Write(Path.Combine("dir", ".hidden"), 50); // Hidden on Unix by its dot.
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var m = ProbeMeasurer.Measure(Probe(ProbeKind.Dir, "dir"), Generous, TestContext.Current.CancellationToken);

        Assert.Equal(400, m.SizeBytes);
        Assert.Equal(3, m.FileCount);
        Assert.Null(m.Error);
    }

    [Fact]
    public void Missing_paths_measure_zero_without_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ProbeMeasurement.Missing with { DurationMs = 0 }, ProbeMeasurer.Measure(Probe(ProbeKind.Dir, "nope"), Generous, ct) with { DurationMs = 0 });
        Assert.Equal(0, ProbeMeasurer.Measure(Probe(ProbeKind.File, Path.Combine("nope", "x.sys")), Generous, ct).SizeBytes);
        Assert.Null(ProbeMeasurer.Measure(Probe(ProbeKind.File, "x.sys"), Generous, ct).Error);
        Assert.Null(ProbeMeasurer.Measure(Probe(ProbeKind.Glob, Path.Combine("*", "x")), Generous, ct).Error);
    }

    [Fact]
    public void A_file_is_sized_from_its_directory_entry_even_when_hidden_and_system()
    {
        var path = Write("pagefile.sys", 4096);
        if (OperatingSystem.IsWindows())
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);

        var m = ProbeMeasurer.Measure(Probe(ProbeKind.File, "pagefile.sys"), Generous, TestContext.Current.CancellationToken);

        Assert.Equal(4096, m.SizeBytes);
        Assert.Equal(1, m.FileCount);
    }

    [Fact]
    public void A_glob_sums_every_match()
    {
        Write(Path.Combine("Packages", "CanonicalGroup.Ubuntu", "LocalState", "ext4.vhdx"), 1000);
        Write(Path.Combine("Packages", "CanonicalGroup.Debian", "LocalState", "ext4.vhdx"), 500);
        Write(Path.Combine("Packages", "Other", "LocalState", "ext4.vhdx"), 77);

        var m = ProbeMeasurer.Measure(Probe(ProbeKind.Glob, Path.Combine("Packages", "*canonicalgroup*", "LocalState")), Generous, TestContext.Current.CancellationToken);

        Assert.Equal(1500, m.SizeBytes);
        Assert.Equal(2, m.FileCount);
    }

    [Fact]
    public void A_per_user_probe_sums_across_every_real_profile()
    {
        Write(Path.Combine("Users", "alice", "Temp", "a.bin"), 300);
        Write(Path.Combine("Users", "bob", "Temp", "b.bin"), 450);
        Write(Path.Combine("Users", "Public", "Temp", "ignored.bin"), 900);
        var expander = new ProbeExpander(Path.Combine(_temp.Path, "Users"));
        var probe = Assert.Single(expander.Expand([new ProbeDefinition { Name = "User Temp", Path = @"{user}\Temp" }]));

        var m = ProbeMeasurer.Measure(probe, Generous, TestContext.Current.CancellationToken, expander);

        Assert.Equal(750, m.SizeBytes);
        Assert.Equal(2, m.FileCount);
        Assert.Null(m.Error);
    }

    [Fact]
    public void A_junction_to_itself_is_not_followed_or_double_counted()
    {
        Write(Path.Combine("loop", "a.bin"), 123);
        var target = Path.Combine(_temp.Path, "loop");
        CreateJunction(Path.Combine(target, "self"), target);

        var m = ProbeMeasurer.Measure(Probe(ProbeKind.Dir, "loop"), Generous, TestContext.Current.CancellationToken);

        Assert.Equal(123, m.SizeBytes);
        Assert.Equal(1, m.FileCount);
        Assert.Null(m.Error);
    }

    [Fact]
    public void A_timeout_records_an_error_and_keeps_the_partial_size()
    {
        for (var i = 0; i < 20; i++)
            Write(Path.Combine("big", $"{i}.bin"), 10);

        var m = ProbeMeasurer.Measure(Probe(ProbeKind.Dir, "big"), TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Contains("timed out", m.Error);
        Assert.Equal(1, m.FileCount);
        Assert.Equal(10, m.SizeBytes);
    }

    [Fact]
    public async Task One_probe_timing_out_does_not_stall_the_others()
    {
        for (var i = 0; i < 20; i++)
            Write(Path.Combine("big", $"{i}.bin"), 10);
        Write(Path.Combine("small", "a.bin"), 5);
        var probes = new[] { Probe(ProbeKind.Dir, "big"), Probe(ProbeKind.Dir, "small"), Probe(ProbeKind.Dir, "missing") };

        // Every probe gets the same zero budget; only the one with more than one file runs out mid-scan.
        var results = await new ProbeRunner(new FakeShadowStorage(), TimeSpan.Zero, maxParallel: 1)
            .RunAsync(probes, TestContext.Current.CancellationToken);

        Assert.Equal(probes, results.Select(r => r.Probe));
        Assert.Contains("timed out", results[0].Measurement.Error);
        Assert.Equal(5, results[1].Measurement.SizeBytes);
        Assert.Equal(0, results[2].Measurement.SizeBytes);
        Assert.Null(results[2].Measurement.Error);
    }

    [Fact]
    public async Task Vss_probes_take_allocated_shadow_storage_read_once_per_run()
    {
        var vss = new FakeShadowStorage(new ShadowStorage("C:", UsedBytes: 1, AllocatedBytes: 5_000, MaxBytes: 9_000));
        var probes = new[]
        {
            new ExpandedProbe("VSS C", ProbeKind.Vss, "C:", "C:"),
            new ExpandedProbe("VSS D", ProbeKind.Vss, "D:", "D:"),
        };

        var results = await new ProbeRunner(vss, Generous).RunAsync(probes, TestContext.Current.CancellationToken);

        Assert.Equal(1, vss.Reads);
        Assert.Equal(5_000, results[0].Measurement.SizeBytes);
        Assert.Equal(0, results[1].Measurement.SizeBytes); // No shadow storage on D: is 0, not an error.
        Assert.All(results, r => Assert.Null(r.Measurement.Error));
    }

    [Fact]
    public async Task A_failing_vss_read_is_recorded_as_an_error()
    {
        var vss = new FakeShadowStorage { Failure = new InvalidOperationException("access denied") };

        var result = Assert.Single(await new ProbeRunner(vss, Generous)
            .RunAsync([new ExpandedProbe("VSS", ProbeKind.Vss, "C:", "C:")], TestContext.Current.CancellationToken));

        Assert.Contains("access denied", result.Measurement.Error);
    }

    [Theory]
    [InlineData(@"Win32_Volume.DeviceID=""\\\\?\\Volume{3f6c1b2e-0000-0000-0000-100000000000}\\""", @"\\?\Volume{3f6c1b2e-0000-0000-0000-100000000000}\")]
    [InlineData(@"\\PC\root\cimv2:Win32_Volume.DeviceID=""\\\\?\\Volume{a}\\""", @"\\?\Volume{a}\")]
    [InlineData("Win32_Volume.Other=1", null)]
    [InlineData(null, null)]
    public void Shadow_storage_volume_references_yield_the_device_id(string? reference, string? expected) =>
        Assert.Equal(expected, ShadowStoragePaths.DeviceId(reference));

    private static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction needs no privilege, unlike a directory symlink.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private sealed class FakeShadowStorage(params ShadowStorage[] storage) : IShadowStorageReader
    {
        public int Reads { get; private set; }
        public Exception? Failure { get; init; }

        public IReadOnlyList<ShadowStorage> Read()
        {
            Reads++;
            return Failure is null ? storage : throw Failure;
        }
    }
}
