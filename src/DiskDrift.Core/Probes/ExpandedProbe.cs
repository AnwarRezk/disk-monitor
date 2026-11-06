using DiskDrift.Core.Config;

namespace DiskDrift.Core.Probes;

/// <summary>
/// One Probe as stored in the <c>probes</c> table. Per-user Probes keep their <c>{user}</c> template in
/// <see cref="Path"/> so no Windows username is written to the database; <see cref="ProbeMeasurer"/> expands it across
/// profiles at each run. A <see cref="ProbeKind.Glob"/> path keeps its wildcards, also resolved at each run.
/// </summary>
public sealed record ExpandedProbe(string Name, ProbeKind Kind, string Path, string Volume)
{
    /// <summary>The value stored in <c>probes.kind</c>: 'dir' | 'file' | 'glob' | 'vss'.</summary>
    public string KindText => Kind.ToString().ToLowerInvariant();
}
