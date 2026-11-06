using System.Diagnostics;
using System.IO.Enumeration;
using DiskDrift.Core.Config;

namespace DiskDrift.Core.Probes;

/// <summary>
/// Measures the logical size of dir, file and glob Probes (vss Probes come from <see cref="IShadowStorageReader"/>).
/// Directory scans recurse, count hidden and system files, never follow junctions or symlinks, skip what they
/// cannot read, and stop at the timeout keeping the partial size.
/// </summary>
public static class ProbeMeasurer
{
    // Only reparse points are skipped: the default (Hidden | System) would miss pagefile.sys and $Recycle.Bin.
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
    };

    public static ProbeMeasurement Measure(
        ExpandedProbe probe,
        TimeSpan timeout,
        CancellationToken token = default,
        ProbeExpander? expander = null)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = new Deadline(started, timeout, token);
        var total = new Total();
        string? error = null;
        var effectiveExpander = expander ?? new ProbeExpander(ProbeExpander.DefaultUsersRoot);
        try
        {
            var paths = probe.Path.Contains(ProbeDefinition.UserTemplate, StringComparison.OrdinalIgnoreCase)
                ? effectiveExpander.ExpandPaths(probe.Path)
                : [probe.Path];
            foreach (var path in paths)
            {
                error = probe.Kind switch
                {
                    ProbeKind.Dir => ScanDirectory(path, deadline, total),
                    ProbeKind.File => SizeFile(path, total),
                    ProbeKind.Glob => MeasureGlob(path, deadline, total),
                    _ => throw new ArgumentException($"{probe.Kind} Probes are not measured on disk", nameof(probe)),
                };
                if (error is not null)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ProbeExpander.ScrubUserPaths(ex.Message, effectiveExpander.UsersRoot);
        }

        token.ThrowIfCancellationRequested();
        return new ProbeMeasurement(total.Bytes, total.Files, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, error);
    }

    private static string? ScanDirectory(string path, Deadline deadline, Total total)
    {
        if (!Directory.Exists(path))
            return null;

        var files = new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length, ScanOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        };
        foreach (var length in files)
        {
            total.Add(length);
            if (deadline.Passed)
                return deadline.Cancelled ? "cancelled" : $"timed out after {deadline.Timeout.TotalSeconds:0.#} s; size is partial";
        }
        return null;
    }

    /// <summary>
    /// Reads the size from the parent's directory entry: pagefile.sys, hiberfil.sys and swapfile.sys cannot be opened,
    /// but their entries can be listed.
    /// </summary>
    private static string? SizeFile(string path, Total total)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !Directory.Exists(parent))
            return null;

        var entry = new DirectoryInfo(parent).EnumerateFiles(name, EntryOptions).FirstOrDefault();
        if (entry is not null)
            total.Add(entry.Length);
        return null;
    }

    private static string? MeasureGlob(string pattern, Deadline deadline, Total total)
    {
        foreach (var match in ProbeExpander.ResolveGlob(pattern))
        {
            var error = Directory.Exists(match) ? ScanDirectory(match, deadline, total) : SizeFile(match, total);
            if (error is not null)
                return error;
            if (deadline.Passed)
                return $"timed out after {deadline.Timeout.TotalSeconds:0.#} s; size is partial";
        }
        return null;
    }

    private sealed class Total
    {
        public long Bytes { get; private set; }
        public long Files { get; private set; }

        public void Add(long length)
        {
            Bytes += length;
            Files++;
        }
    }

    private readonly record struct Deadline(long Started, TimeSpan Timeout, CancellationToken Token)
    {
        public bool Cancelled => Token.IsCancellationRequested;
        public bool Passed => Cancelled || Stopwatch.GetElapsedTime(Started) >= Timeout;
    }
}
