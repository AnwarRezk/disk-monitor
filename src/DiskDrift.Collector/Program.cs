using System.Security.Principal;
using DiskDrift.Collector.Samplers;
using DiskDrift.Collector.Services;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Logging;
using DiskDrift.Core.Probes;
using DiskDrift.Core.Sampling;
using DiskDrift.Core.Security;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;

// The Collector: an elevated Windows Service (LocalSystem), or a console app when started from a terminal.
// It is the only writer of the DiskDrift database.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // appsettings.json sits next to the exe; a service's working directory is System32.
    ContentRootPath = AppContext.BaseDirectory,
});

var loggingReady = false;
try
{
    var options = DiskDriftOptions.Bind(builder.Configuration);

    // The data folder's ACL only lets SYSTEM and Administrators write. A non-elevated run would create
    // the folder and then be unable to write its own logs and DB, so refuse up front.
    if (!IsElevated())
    {
        Console.Error.WriteLine("DiskDrift Collector must run elevated (as a service, or from an elevated terminal).");
        return 1;
    }

    // Create the data folder (with its locked-down ACL) before anything, including logs, is written into it.
    var folderCreated = DataFolder.EnsureCreated(options.DataDirectory);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("SourceContext", "DiskDrift.Collector")
        .WriteTo.Console()
        .WriteTo.CappedFile(options.LogDirectory, "collector")
        .CreateLogger();
    loggingReady = true;

    Log.Information("DiskDrift Collector {Version} starting", typeof(Program).Assembly.GetName().Version);
    if (folderCreated)
        Log.Information("Created data folder {DataDirectory} (SYSTEM + Administrators full control, Users read-only)", options.DataDirectory);

    var db = Db.OpenOrCreate(options.DatabasePath);
    foreach (var migration in db.AppliedMigrations)
        Log.Information("Applied migration {Version:0000} {Name}", migration.Version, migration.Name);
    Log.Information("Database {DatabasePath} at schema version {SchemaVersion}", db.Path, Migrator.LatestVersion);
    Log.Information("Volumes: {Volumes}; {ProbeCount} Probes configured", options.Volumes, options.Probes.Count);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(db);
    builder.Services.AddSingleton<IDbConnectionSource>(db);
    builder.Services.AddSingleton(TimeProvider.System);

    // Probes, VSS shadow storage included (DIS-12): every 10 min, on demand (probe_now over the pipe) and when a
    // Swing opens or closes.
    builder.Services.AddSingleton<IShadowStorageReader, VssSampler>();
    builder.Services.AddSingleton<ProbeSampler>();

    // Swing detection (DIS-13). Registered before the VolumeSampler: hosted services start in order, and the
    // detector must reload the previous run's samples before this run stores its first one.
    builder.Services.AddSingleton<VolumeSampleChannel>();
    builder.Services.AddHostedService<SwingService>();

    // Volume Samples (DIS-7): the free-space timeline, also handed to Swing detection through the channel.
    builder.Services.AddSingleton<VolumeSampleStore>();
    builder.Services.AddHostedService<VolumeSampler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbeSampler>());

    // Collector status for the Tray over the named pipe (DIS-10).
    builder.Services.AddSingleton<CollectorHeartbeat>();
    builder.Services.AddHostedService<PipeServer>();

    builder.Services.AddWindowsService(o => o.ServiceName = "DiskDrift");
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<ProcessIoSampler>();
    builder.Services.AddHostedService<RetentionService>();

    // Hold one connection for the Collector's lifetime so the -wal/-shm files always exist while it runs:
    // the Tray's read-only reader needs them, and only a stopped Collector sends it to the immutable fallback.
    // (Pooled connections alone may all be pruned between samples, which would delete those files.)
    using var keepAlive = db.OpenConnection();

    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    if (loggingReady)
        Log.Fatal(ex, "DiskDrift Collector failed");
    else
        Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    // Closing every pooled connection lets the last close checkpoint the WAL and delete -wal/-shm, so the
    // Tray's immutable fallback sees all history while the Collector is stopped.
    SqliteConnection.ClearAllPools();
    await Log.CloseAndFlushAsync();
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
