using System.Diagnostics;
using DiskMon.Core.Analysis;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Ipc;
using Microsoft.Data.Sqlite;

namespace DiskMon.Tray;

/// <summary>
/// Hidden application context owning the tray icon. Every 30 s it reads the newest Volume Sample from the
/// database (read-only, never written), pings the Collector over the pipe, and redraws the icon and tooltip:
/// grey with "Collector not running" when the ping fails or reports no sample in the last 2 minutes.
/// The dashboard arrives in a later ticket.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DiskMonOptions _options;
    private readonly string _volume;
    private readonly VolumeSampleStore _samples;
    private readonly ReadOnlyDb _db;
    private readonly PipeClient _pipe = new();
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private Icon? _currentIcon;
    private bool _refreshing;

    public TrayContext(TrayConfig config)
    {
        _options = config.Options;
        // C: is the default and priority Volume; if config leaves it out, show the first sampled one.
        _volume = _options.Volumes.Contains(DiskMonOptions.DefaultVolume) ? DiskMonOptions.DefaultVolume : _options.Volumes[0];
        _db = new ReadOnlyDb(_options.DatabasePath);
        _samples = new VolumeSampleStore(_db);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
        ShowUnknown("DiskMon: reading free space…");
        _icon.Visible = true;

        if (config.Error is not null)
            _icon.ShowBalloonTip(10_000, "DiskMon config ignored", $"{config.SourcePath}: {config.Error}", ToolTipIcon.Warning);

        _timer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var ping = Task.Run(PingAsync);
            var reading = await Task.Run(Read);
            var running = CollectorStatus.IsRunning(await ping, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (reading is null)
                ShowUnknown($"DiskMon: no samples for {_volume} yet" + (running ? "" : "\nCollector not running"));
            else
                Show(reading.Value.View, running, reading.Value.LatestTs);
        }
        catch (SqliteException ex)
        {
            ShowUnknown(File.Exists(_db.Path)
                ? $"DiskMon: cannot read the database ({ex.SqliteErrorCode})"
                : "DiskMon: no database yet. Is the Collector installed?");
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>The Collector's <c>ping</c> answer, or null when it is unreachable.</summary>
    private async Task<PipeResponse?> PingAsync()
    {
        try
        {
            return await _pipe.PingAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private (FreeSpaceView View, long LatestTs)? Read()
    {
        var latest = _samples.Latest(_volume);
        if (latest is null)
            return null;

        var windowStart = latest.Ts - (long)FreeSpaceStatus.ChangeWindow.TotalMilliseconds;
        var baseline = FreeSpaceStatus.ChangeBaseline(latest, _samples.AtOrBefore(_volume, windowStart), _samples.Oldest(_volume));
        return (FreeSpaceStatus.Describe(latest, baseline, _options.Tray), latest.Ts);
    }

    private void Show(FreeSpaceView view, bool collectorRunning, long latestTs)
    {
        if (collectorRunning)
        {
            SetIcon(TrayIconRenderer.Render(view.Level, view.IconLabel), view.Tooltip);
            return;
        }

        // Keep the last known number, but grey: it may be stale.
        var tooltip = view.Tooltip + $"\nCollector not running (as of {DateTimeOffset.FromUnixTimeMilliseconds(latestTs).LocalDateTime:g})";
        SetIcon(TrayIconRenderer.RenderStale(view.IconLabel), tooltip);
    }

    private void ShowUnknown(string tooltip) => SetIcon(TrayIconRenderer.RenderUnknown(), tooltip);

    private void SetIcon(Icon icon, string tooltip)
    {
        var previous = _currentIcon;
        _icon.Icon = icon;
        _icon.Text = tooltip.Length <= FreeSpaceStatus.MaxTooltipLength ? tooltip : tooltip[..FreeSpaceStatus.MaxTooltipLength];
        _currentIcon = icon;
        previous?.Dispose();
    }

    private static void OpenDashboard() =>
        MessageBox.Show("The dashboard is coming in a later version of DiskMon.", "DiskMon", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void OpenDataFolder()
    {
        if (Directory.Exists(_options.DataDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_options.DataDirectory}\"") { UseShellExecute = false });
        else
            MessageBox.Show($"{_options.DataDirectory} does not exist yet. Is the Collector installed?", "DiskMon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
