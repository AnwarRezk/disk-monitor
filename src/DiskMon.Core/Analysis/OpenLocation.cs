namespace DiskMon.Core.Analysis;

/// <summary>What Explorer should show: a folder opened, or a file selected in its folder.</summary>
public sealed record ExplorerTarget(string Path, bool SelectFile)
{
    /// <summary>The <c>explorer.exe</c> arguments: <c>/select,"file"</c> or <c>"folder"</c>.</summary>
    public string Arguments => SelectFile ? $"/select,\"{Path}\"" : $"\"{Path}\"";
}

/// <summary>
/// Resolves a Swing item's path for "Open location" (PLAN §5.6): an existing file is selected, a folder is opened,
/// and a path that no longer exists falls back to its nearest existing parent. Glob paths fall back the same way
/// (a wildcard segment never exists). The file system is passed in, so this stays pure.
/// </summary>
public static class OpenLocation
{
    public static ExplorerTarget? Resolve(string? path, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var current = path.Trim().TrimEnd('\\', '/');
        // "C:" alone (the VSS Probe's path) means the drive's current folder to Windows: open its root instead.
        if (current.Length == 2 && current[1] == ':')
            current += "\\";

        if (fileExists(current))
            return new ExplorerTarget(current, SelectFile: true);
        while (!string.IsNullOrEmpty(current))
        {
            if (directoryExists(current))
                return new ExplorerTarget(current, SelectFile: false);
            current = Parent(current);
        }
        return null;
    }

    private static string Parent(string path)
    {
        var cut = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
        if (cut < 0)
            return "";
        // Keep the root's separator: the parent of C:\Users is C:\.
        return cut == 2 && path[1] == ':' ? path[..3] : path[..cut];
    }
}
