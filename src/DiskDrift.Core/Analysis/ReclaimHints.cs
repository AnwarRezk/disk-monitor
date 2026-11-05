using System.Text.RegularExpressions;

namespace DiskDrift.Core.Analysis;

/// <summary>How to win space back from a Probe: a command to copy (when one is safe to suggest) and a short note.</summary>
public sealed record ReclaimHint(string? Command, string Note);

/// <summary>
/// Reclaim hints for the default Probes, keyed by their configured name. Per-user Probes are stored as
/// "Name (user)", so the suffix is ignored. Commands are only suggested, never run.
/// </summary>
public static partial class ReclaimHints
{
    private static readonly Dictionary<string, ReclaimHint> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WSL disks"] = new("wsl --manage <distro> --set-sparse true", "then wsl --shutdown so the disk shrinks"),
        ["Docker"] = new("docker system prune -a", "then Docker Desktop › Troubleshoot › Clean / Purge data"),
        ["User Temp"] = new("cleanmgr", "Disk Cleanup › Temporary files; close apps first"),
        ["Windows Temp"] = new("cleanmgr", "Disk Cleanup › Temporary files (admin)"),
        ["Windows Update downloads"] = new("cleanmgr", "Disk Cleanup › Clean up system files › Windows Update Cleanup"),
        ["Delivery Optimization"] = new("cleanmgr", "Disk Cleanup › Delivery Optimization Files"),
        ["Windows upgrade staging"] = new("cleanmgr", "Disk Cleanup › Previous Windows installations"),
        ["Recycle Bin"] = new("Clear-RecycleBin -Force", "PowerShell; empties every drive's bin"),
        ["npm cache"] = new("npm cache clean --force", "packages download again when needed"),
        ["pnpm store"] = new("pnpm store prune", "drops packages no project uses"),
        ["Yarn cache"] = new("yarn cache clean", ""),
        ["pip cache"] = new("pip cache purge", ""),
        ["NuGet packages"] = new("dotnet nuget locals global-packages --clear", "packages restore again on the next build"),
        ["Gradle"] = new(null, "delete ~/.gradle/caches with no Gradle build running"),
        ["Maven"] = new(null, "delete unused versions under ~/.m2/repository"),
        ["Crash dumps"] = new(null, "safe to delete once nobody needs the dumps"),
        ["Minidumps"] = new(null, "safe to delete once nobody needs the dumps"),
        ["Memory dump"] = new(null, "safe to delete once nobody needs the dump"),
        ["Live kernel reports"] = new(null, "safe to delete once nobody needs the reports"),
        ["Hibernation file"] = new("powercfg /hibernate off", "admin; turns off hibernation and Fast Startup"),
        ["Page file"] = new(null, "managed by Windows"),
        ["Swap file"] = new(null, "managed by Windows"),
        ["VSS shadow storage"] = new("vssadmin resize shadowstorage /for=C: /on=C: /maxsize=5%", "admin; keeps fewer restore points"),
        ["Windows Installer cache"] = new(null, "don't delete by hand; repairs and uninstalls need it"),
        ["Downloads"] = new(null, "review by hand"),
        ["Chrome"] = new(null, "Settings › Privacy › Delete browsing data › Cached files"),
        ["Edge"] = new(null, "Settings › Privacy › Clear browsing data › Cached files"),
        ["JetBrains"] = new(null, "delete folders of old IDE versions"),
        ["VS Code"] = new(null, "remove unused extensions; clear Cache and CachedData"),
        ["Cursor"] = new(null, "remove unused extensions; clear Cache and CachedData"),
    };

    /// <summary>The hint for a stored Probe name, or null when there is none.</summary>
    public static ReclaimHint? For(string probeName) =>
        ByName.TryGetValue(BaseName(probeName), out var hint) ? hint : null;

    /// <summary>"User Temp (shado)" → "User Temp".</summary>
    public static string BaseName(string probeName) => UserSuffix().Replace(probeName.Trim(), "");

    [GeneratedRegex(@"\s*\([^()]*\)$")]
    private static partial Regex UserSuffix();
}
