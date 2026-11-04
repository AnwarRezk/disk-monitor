using DiskMon.Core.Config;
using Microsoft.Extensions.Configuration;

namespace DiskMon.Tray;

/// <summary>The Tray's view of the shared config: the Collector's appsettings.json, read but never written.</summary>
internal sealed record TrayConfig(DiskMonOptions Options, string? SourcePath, string? Error)
{
    /// <summary>
    /// Reads the first appsettings.json found next to the Tray exe or in the installed Collector folder
    /// (<c>..\Collector</c>, the layout install-service.ps1 creates). Without one, or if it is invalid,
    /// the defaults apply and <see cref="Error"/> says why.
    /// </summary>
    public static TrayConfig Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "appsettings.json"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Collector", "appsettings.json")),
        };

        var source = candidates.FirstOrDefault(File.Exists);
        if (source is null)
            return new TrayConfig(Defaults(), null, null);

        try
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(source, optional: false).Build();
            return new TrayConfig(DiskMonOptions.Bind(configuration), source, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
        {
            return new TrayConfig(Defaults(), source, ex.Message);
        }
    }

    private static DiskMonOptions Defaults() => DiskMonOptions.Bind(new ConfigurationBuilder().Build());
}
