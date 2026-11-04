using System.Security.Principal;
using DiskMon.Collector.Samplers;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Logging;
using DiskMon.Core.Sampling;
using DiskMon.Core.Security;
using Serilog;
using Serilog.Events;

// The Collector: an elevated Windows Service (LocalSystem), or a console app when started from a terminal.
// It is the only writer of the DiskMon database.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // appsettings.json sits next to the exe; a service's working directory is System32.
    ContentRootPath = AppContext.BaseDirectory,
});

var loggingReady = false;
try
{
    var options = DiskMonOptions.Bind(builder.Configuration);

    // The data folder's ACL only lets SYSTEM and Administrators write. A non-elevated run would create
    // the folder and then be unable to write its own logs and DB, so refuse up front.
    if (!IsElevated())
    {
        Console.Error.WriteLine("DiskMon Collector must run elevated (as a service, or from an elevated terminal).");
        return 1;
    }

    // Create the data folder (with its locked-down ACL) before anything, including logs, is written into it.
    var folderCreated = DataFolder.EnsureCreated(options.DataDirectory);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("SourceContext", "DiskMon.Collector")
        .WriteTo.Console()
        .WriteTo.CappedFile(options.LogDirectory, "collector")
        .CreateLogger();
    loggingReady = true;

    Log.Information("DiskMon Collector {Version} starting", typeof(Program).Assembly.GetName().Version);
    if (folderCreated)
        Log.Information("Created data folder {DataDirectory} (SYSTEM + Administrators full control, Users read-only)", options.DataDirectory);

    var db = Db.OpenOrCreate(options.DatabasePath);
    foreach (var migration in db.AppliedMigrations)
        Log.Information("Applied migration {Version:0000} {Name}", migration.Version, migration.Name);
    Log.Information("Database {DatabasePath} at schema version {SchemaVersion}", db.Path, Migrator.LatestVersion);
    Log.Information("Volumes: {Volumes}; {ProbeCount} Probes configured", options.Volumes, options.Probes.Count);

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(db);
    builder.Services.AddSingleton(TimeProvider.System);

    // Volume Samples (DIS-7): the free-space timeline, also handed to Swing detection through the channel.
    builder.Services.AddSingleton<VolumeSampleStore>();
    builder.Services.AddSingleton<VolumeSampleChannel>();
    builder.Services.AddHostedService<VolumeSampler>();

    builder.Services.AddWindowsService(o => o.ServiceName = "DiskMon");
    builder.Services.AddSerilog();

    // Samplers and services arrive in M1–M3.
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    if (loggingReady)
        Log.Fatal(ex, "DiskMon Collector failed");
    else
        Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}
