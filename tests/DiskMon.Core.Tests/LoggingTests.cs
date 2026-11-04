using DiskMon.Core.Logging;
using Serilog;

namespace DiskMon.Core.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Production_cap_is_7_files_of_10_MB()
    {
        Assert.Equal(10L * 1024 * 1024, DiskMonLogging.FileSizeLimitBytes);
        Assert.Equal(7, DiskMonLogging.RetainedFileCount);
    }

    [Fact]
    public void Logs_roll_on_size_and_keep_only_the_retained_file_count()
    {
        const long limit = 1024;
        const int retained = 7;

        using (var logger = new LoggerConfiguration()
                   .WriteTo.CappedFile(_temp.Path, "test", fileSizeLimitBytes: limit, retainedFileCount: retained)
                   .CreateLogger())
        {
            for (var i = 0; i < 2000; i++)
                logger.Information("Line {Index} padded to fill the file quickly: {Padding}", i, new string('x', 80));
        }

        var files = new DirectoryInfo(_temp.Path).GetFiles("test-*.log");
        Assert.Equal(retained, files.Length);
        // A file may overshoot by at most one event before rolling.
        Assert.All(files, f => Assert.True(f.Length <= limit + 512, $"{f.Name} is {f.Length} bytes"));
    }
}
