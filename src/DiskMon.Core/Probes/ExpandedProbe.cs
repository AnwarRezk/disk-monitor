using DiskMon.Core.Config;

namespace DiskMon.Core.Probes;

/// <summary>
/// One concrete Probe, as stored in the <c>probes</c> table: a configured Probe with <c>{user}</c> already replaced by
/// a profile folder. A <see cref="ProbeKind.Glob"/> path still holds its wildcards, which are resolved at every run.
/// </summary>
public sealed record ExpandedProbe(string Name, ProbeKind Kind, string Path, string Volume)
{
    /// <summary>The value stored in <c>probes.kind</c>: 'dir' | 'file' | 'glob' | 'vss'.</summary>
    public string KindText => Kind.ToString().ToLowerInvariant();
}
