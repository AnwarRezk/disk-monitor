using System.Diagnostics;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Ipc;
using DiskDrift.Tray.Dashboard;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Tray;

/// <summary>
/// Hidden application context owning the tray icon. Every 30 s it reads the newest Volume Sample from the
/// database (read-only, never written), pings the Collector over the pipe, and redraws the icon and tooltip:
/// grey with "Collector not running" when the ping fails or reports no sample in the last 2 minutes.
/// "Open dashboard" (or double-clicking the icon) opens the <see cref="DashboardForm"/>, one window at a time, and so
/// does a later launch with <c>--dashboard</c> (the shortcuts), which signals the running Tray instead of starting a second one.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DiskDriftOptions _options;
    private readonly string _volume;
    private readonly VolumeSampleStore _samples;
    private readonly ReadOnlyDb _db;
    private readonly PipeClient _pipe = new();
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _probeNow;
    private Icon? _currentIcon;
    private readonly Control _uiThread = new();
    private readonly RegisteredWaitHandle _dashboardRequests;
    private DashboardForm? _dashboard;
    private bool _refreshing;

    public TrayContext(TrayConfig config, WaitHandle dashboardRequest, bool openDashboard = false)
    {
        // A hidden control's handle lets the wait callback below marshal onto the UI thread.
        _uiThread.CreateControl();
        _dashboardRequests = ThreadPool.RegisterWaitForSingleObject(dashboardRequest, (_, _) =>
        {
            if (_uiThread.IsHandleCreated)
                _uiThread.BeginInvoke(OpenDashboard);
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        _options = config.Options;
        // C: is the default and priority Volume; if config leaves it out, show the first sampled one.
        _volume = _options.Volumes.Contains(DiskDriftOptions.DefaultVolume) ? DiskDriftOptions.DefaultVolume : _options.Volumes[0];
        _db = new ReadOnlyDb(_options.DatabasePath);
        _samples = new VolumeSampleStore(_db);

        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Raised,
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new MenuColors()),
        };
        var openItem = new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenDashboard()) { ForeColor = Theme.Text, Font = Theme.BodyStrong };
        menu.Items.Add(openItem);
        _probeNow = new ToolStripMenuItem("Probe now", null, async (_, _) => await ProbeNowAsync()) { ForeColor = Theme.Text, Font = Theme.Body };
        menu.Items.Add(_probeNow);
        menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenDataFolder()) { ForeColor = Theme.Text, Font = Theme.Body });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()) { ForeColor = Theme.Text, Font = Theme.Body });

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = false };
        _icon.DoubleClick += (_, _) => OpenDashboard();
        ShowUnknown("DiskDrift: reading free space…");
        _icon.Visible = true;

        if (config.Error is not null)
            _icon.ShowBalloonTip(10_000, "DiskDrift config ignored", $"{config.SourcePath}: {config.Error}", ToolTipIcon.Warning);

        _timer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
        if (openDashboard)
            OpenDashboard();
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
                ShowUnknown($"DiskDrift: no samples for {_volume} yet" + (running ? "" : "\nCollector not running"));
            else
                Show(reading.Value.View, running, reading.Value.LatestTs);
        }
        catch (SqliteException ex)
        {
            ShowUnknown(File.Exists(_db.Path)
                ? $"DiskDrift: cannot read the database ({ex.SqliteErrorCode})"
                : "DiskDrift: no database yet. Is the Collector installed?");
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

    /// <summary>
    /// Asks the Collector to measure every Probe and says when it is done. The item stays disabled meanwhile, and
    /// the Collector joins overlapping requests into one run anyway.
    /// </summary>
    private async Task ProbeNowAsync()
    {
        if (!_probeNow.Enabled)
            return;
        _probeNow.Enabled = false;
        _probeNow.Text = "Probing…";
        try
        {
            var response = await Task.Run(() => _pipe.ProbeNowAsync());
            if (response.Ok)
            {
                var errors = response.Errors is > 0 ? $", {response.Errors} with errors (see the Collector log)" : "";
                _icon.ShowBalloonTip(5_000, "DiskDrift", $"Probe run finished: {response.Probes} Probes in {(response.DurationMs ?? 0) / 1000.0:0.#} s{errors}.", ToolTipIcon.Info);
            }
            else
            {
                _icon.ShowBalloonTip(10_000, "DiskDrift", $"Probe run failed: {response.Error}", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _icon.ShowBalloonTip(10_000, "DiskDrift", $"Probe now needs the Collector, which did not answer ({ex.Message}).", ToolTipIcon.Warning);
        }
        finally
        {
            _probeNow.Text = "Probe now";
            _probeNow.Enabled = true;
        }
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

    private void OpenDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(new DashboardLoader(_db, _options), _pipe, _volume);
            _dashboard.FormClosed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        if (_dashboard.WindowState == FormWindowState.Minimized)
            _dashboard.WindowState = FormWindowState.Normal;
        _dashboard.Activate();
    }

    private void OpenDataFolder()
    {
        if (Directory.Exists(_options.DataDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_options.DataDirectory}\"") { UseShellExecute = false });
        else
            MessageBox.Show($"{_options.DataDirectory} does not exist yet. Is the Collector installed?", "DiskDrift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void ExitThreadCore()
    {
        _dashboardRequests.Unregister(null);
        _timer.Stop();
        _timer.Dispose();
        _dashboard?.Close();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _currentIcon?.Dispose();
        _uiThread.Dispose();
        base.ExitThreadCore();
    }
}
