using System.IO.Enumeration;
using DiskMon.Core.Config;

namespace DiskMon.Core.Probes;

/// <summary>
/// Turns the configured Probes into concrete ones. The Collector runs as SYSTEM, so <c>%LOCALAPPDATA%</c> and friends
/// point at the system profile; per-user Probes use <c>{user}</c> instead, expanded over every profile folder under
/// <see cref="UsersRoot"/> except the built-in ones. Pure file-system reads, no Win32 calls.
/// </summary>
public sealed class ProbeExpander(string usersRoot)
{
    /// <summary>The profile root on the default and priority Volume.</summary>
    public const string DefaultUsersRoot = @"C:\Users";

    /// <summary>Folders under <c>C:\Users</c> that are not real people.</summary>
    public static readonly IReadOnlySet<string> ExcludedProfiles =
        new HashSet<string>(["Default", "Default User", "Public", "All Users"], StringComparer.OrdinalIgnoreCase);

    public string UsersRoot { get; } = usersRoot;

    /// <summary>
    /// Every configured Probe with <c>{user}</c> replaced once per profile (named "Name (user)"), duplicates by
    /// kind and path dropped. Separators are normalised so the same config works in tests on any OS.
    /// </summary>
    public IReadOnlyList<ExpandedProbe> Expand(IEnumerable<ProbeDefinition> definitions)
    {
        var profiles = UserProfiles();
        var seen = new HashSet<(ProbeKind, string)>(new KindPathComparer());
        var result = new List<ExpandedProbe>();
        foreach (var definition in definitions)
        {
            if (definition.Kind == ProbeKind.Vss)
            {
                var volume = definition.Path.Trim().TrimEnd('\\').ToUpperInvariant();
                Add(new ExpandedProbe(definition.Name, ProbeKind.Vss, volume, volume));
                continue;
            }

            if (!definition.IsPerUser)
            {
                Add(Concrete(definition.Name, definition.Kind, definition.Path));
                continue;
            }

            foreach (var profile in profiles)
            {
                var path = definition.Path.Replace(ProbeDefinition.UserTemplate, profile, StringComparison.OrdinalIgnoreCase);
                Add(Concrete($"{definition.Name} ({System.IO.Path.GetFileName(profile)})", definition.Kind, path));
            }
        }
        return result;

        void Add(ExpandedProbe probe)
        {
            if (seen.Add((probe.Kind, probe.Path)))
                result.Add(probe);
        }
    }

    /// <summary>
    /// The profile folders under <see cref="UsersRoot"/>, sorted, leaving out <see cref="ExcludedProfiles"/>, plain
    /// files and junctions (Default User and All Users are junctions on a real machine).
    /// </summary>
    public IReadOnlyList<string> UserProfiles()
    {
        var root = new DirectoryInfo(NormalizeSeparators(UsersRoot));
        if (!root.Exists)
            return [];

        return root
            .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(d => !ExcludedProfiles.Contains(d.Name))
            .Select(d => d.FullName)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The existing files and folders matching <paramref name="pattern"/>, whose segments may hold <c>*</c> and
    /// <c>?</c> (case-insensitive), e.g. <c>…\Packages\*CanonicalGroup*\LocalState</c>. Junctions are not matched.
    /// </summary>
    public static IReadOnlyList<string> ResolveGlob(string pattern)
    {
        pattern = NormalizeSeparators(pattern);
        var root = System.IO.Path.GetPathRoot(pattern);
        if (string.IsNullOrEmpty(root))
            return [];

        var segments = pattern[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            current = segment.IndexOfAny(['*', '?']) < 0
                ? current.Select(p => System.IO.Path.Combine(p, segment)).Where(p => last ? Exists(p) : Directory.Exists(p))
                : current.SelectMany(p => Matches(p, segment, includeFiles: last));
        }
        return current.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"C:" for a path on C:, or "" for a path without a drive letter (only in tests off Windows).</summary>
    public static string VolumeOf(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) + ":" : "";

    internal static string NormalizeSeparators(string path) =>
        path.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static ExpandedProbe Concrete(string name, ProbeKind kind, string path)
    {
        path = NormalizeSeparators(path.Trim());
        if (path.Length > 1)
            path = path.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return new ExpandedProbe(name, kind, path, VolumeOf(path));
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static IEnumerable<string> Matches(string directory, string segment, bool includeFiles)
    {
        if (!Directory.Exists(directory))
            return [];

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var entries = new FileSystemEnumerable<string>(directory, (ref FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                (includeFiles || e.IsDirectory) && FileSystemName.MatchesSimpleExpression(segment, e.FileName, ignoreCase: true),
        };
        try
        {
            return entries.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class KindPathComparer : IEqualityComparer<(ProbeKind Kind, string Path)>
    {
        public bool Equals((ProbeKind Kind, string Path) x, (ProbeKind Kind, string Path) y) =>
            x.Kind == y.Kind && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ProbeKind Kind, string Path) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }
}
