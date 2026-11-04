using Serilog;
using Serilog.Configuration;

namespace DiskMon.Core.Logging;

/// <summary>
/// Rolling log files with a hard cap, so DiskMon never becomes a source of disk growth itself:
/// a new file per day or per 10 MB, and only the newest 7 files are kept (at most ~70 MB).
/// </summary>
public static class DiskMonLogging
{
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;
    public const int RetainedFileCount = 7;

    public static LoggerConfiguration CappedFile(
        this LoggerSinkConfiguration sink,
        string logDirectory,
        string baseName,
        long fileSizeLimitBytes = FileSizeLimitBytes,
        int retainedFileCount = RetainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        return sink.File(
            Path.Combine(logDirectory, baseName + "-.log"),
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: fileSizeLimitBytes,
            retainedFileCountLimit: retainedFileCount,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }
}
