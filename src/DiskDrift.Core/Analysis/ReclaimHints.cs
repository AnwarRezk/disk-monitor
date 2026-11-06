using System.Text.RegularExpressions;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Analysis;

/// <summary>How to win space back from a Probe: a command to copy (when one is safe to suggest) and a short note.</summary>
public sealed record ReclaimHint(string? Command, string Note);

/// <summary>One step in the virtual-disk reclaim sequence: a short label, a copyable command, and a detail note.</summary>
public sealed record ReclaimStep(string Label, string Command, string Detail);

/// <summary>
/// Callout shown when virtual-disk Probes (WSL, Docker <c>.vhdx</c> files) are large or growing, with the reclaim steps
/// from <c>docs/space-findings.md</c>.
/// </summary>
public sealed record VirtualDiskCallout(
    IReadOnlyList<ProbeSize> Probes,
    long TotalBytes,
    long TotalChangeBytes,
    string Headline,
    string Explanation,
    IReadOnlyList<ReclaimStep> Steps);

/// <summary>
/// Reclaim hints for the default Probes, keyed by their configured name (with any legacy <c>(user)</c> suffix
/// ignored). Commands are only suggested, never run.
/// </summary>
public static partial class ReclaimHints
{
    /// <summary>A virtual-disk Probe at or above 10 GB is large enough to surface the reclaim callout.</summary>
    public const long VirtualDiskLargeBytes = 10 * Bytes.GB;

    /// <summary>A virtual-disk Probe that grew by 500 MB or more in the range surfaces the reclaim callout even below 10 GB.</summary>
    public const long VirtualDiskGrowthBytes = 500 * Bytes.MB;

    private const string WslDisksName = "WSL disks";
    private const string DockerName = "Docker";

    private static readonly Dictionary<string, ReclaimHint> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        [WslDisksName] = new("wsl --manage <distro> --set-sparse true", "then wsl --shutdown so the disk shrinks"),
        [DockerName] = new("docker system prune -a", "then Docker Desktop › Troubleshoot › Clean / Purge data"),
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

    /// <summary>True when a Probe tracks WSL or Docker virtual disks (<c>.vhdx</c> files that grow without shrinking).</summary>
    public static bool IsVirtualDisk(string probeName, string? path = null)
    {
        var baseName = BaseName(probeName);
        return baseName.Equals(WslDisksName, StringComparison.OrdinalIgnoreCase)
            || baseName.Equals(DockerName, StringComparison.OrdinalIgnoreCase)
            || path?.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Builds a virtual-disk reclaim callout when any virtual-disk Probe (WSL disks, Docker) is large (≥ 10 GB) or
    /// growing (≥ 500 MB in the range), or returns null when none qualifies.
    /// </summary>
    public static VirtualDiskCallout? VirtualDiskSummary(IReadOnlyList<ProbeSize> probes)
    {
        var matching = probes
            .Where(p => p.SizeBytes > 0
                && IsVirtualDisk(p.Name, p.Path)
                && (p.SizeBytes >= VirtualDiskLargeBytes || p.ChangeBytes >= VirtualDiskGrowthBytes))
            .ToList();
        if (matching.Count == 0)
            return null;

        var totalBytes = matching.Sum(p => p.SizeBytes);
        var totalChange = matching.Sum(p => p.ChangeBytes ?? 0);
        var hasWsl = matching.Any(p => BaseName(p.Name).Equals(WslDisksName, StringComparison.OrdinalIgnoreCase));
        var hasDocker = matching.Any(p => BaseName(p.Name).Equals(DockerName, StringComparison.OrdinalIgnoreCase));

        var subject = (hasWsl, hasDocker) switch
        {
            (true, true) => "WSL and Docker virtual disks",
            (true, false) => "WSL virtual disks",
            (false, true) => "Docker virtual disks",
            _ => "Virtual disks",
        };
        var changeSuffix = totalChange >= Bytes.MB
            ? $" (+{(totalChange >= Bytes.GB ? SizeText.Gb(totalChange) : SizeText.Compact(totalChange))})"
            : "";
        var headline = $"{subject} hold {SizeText.Gb(totalBytes)}{changeSuffix}";
        const string explanation =
            "A .vhdx grows when Linux or containers write data and does not shrink on its own when files are deleted, "
            + "so free space falls steadily without triggering a Swing. Close Docker Desktop and run wsl --shutdown before reclaiming space.";

        var steps = new List<ReclaimStep>
        {
            new("Stop WSL", "wsl --shutdown", "Close Docker Desktop first, then shut down WSL before shrinking virtual disks"),
        };
        if (hasDocker)
            steps.Add(new("Docker", "docker system prune -a", "Add --volumes only if you don't need volumes, then use Docker Desktop › Troubleshoot › Clean / Purge data"));
        if (hasWsl || !hasDocker)
            steps.Add(new("WSL sparse", "wsl --manage <distro> --set-sparse true", "Run once per distro so freed space is returned; on Hyper-V editions: Optimize-VHD -Path <file.vhdx> -Mode Full"));

        return new VirtualDiskCallout(matching, totalBytes, totalChange, headline, explanation, steps);
    }

    /// <summary>"User Temp (alex)" → "User Temp".</summary>
    public static string BaseName(string probeName) => UserSuffix().Replace(probeName.Trim(), "");

    [GeneratedRegex(@"\s*\([^()]*\)$")]
    private static partial Regex UserSuffix();
}
