namespace DiskMon.Core.Config;

/// <summary>Stored lower-case in <c>probes.kind</c>: 'dir' | 'file' | 'glob' | 'vss'.</summary>
public enum ProbeKind
{
    Dir,
    File,
    Glob,
    Vss,
}
