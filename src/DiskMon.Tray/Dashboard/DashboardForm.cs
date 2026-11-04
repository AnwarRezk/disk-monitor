using System.Diagnostics;
using System.Globalization;
using DiskMon.Core;
using DiskMon.Core.Analysis;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Ipc;
using DiskMon.Core.Native;
using Microsoft.Data.Sqlite;
using ScottPlot;
using ScottPlot.WinForms;
using Alignment = ScottPlot.Alignment;
using Color = System.Drawing.Color;
using HorizontalAlignment = System.Windows.Forms.HorizontalAlignment;
using Label = System.Windows.Forms.Label;
using SpColor = ScottPlot.Color;

namespace DiskMon.Tray.Dashboard;

/// <summary>
/// The dashboard window ("Violet Pro", option B of the 2026-11-04 redesign): a header band with the Volume tabs,
/// Collector status and "Probe now"; a summary row with a ring gauge of used space, the change, the lowest point, a
/// "Full in" forecast and the Swing count; a chart with warning lines, Swing spans, a hover read-out, drag-to-zoom and
/// a lane of Swing chips; and tabs for where the space is (tiles and Probes with reclaim hints), Swings and Top
/// Writers. A Swing opens in a drawer on the right, from its chip, its span on the chart or its row.
/// Reads the database only (PLAN §5.6), so it works with the Collector stopped. Lays itself out from the
/// minimum size up to maximised, and reopens with the last Volume, range, tab and window placement.
/// </summary>
internal sealed class DashboardForm : Form
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private const string SpaceTab = "space", SwingsTab = "swings", WritersTab = "writers";
    private const int HeaderHeight = 60;
    private const int SummaryHeight = 118;
    private const int DrawerWidth = 440;
    private const string Loading = "Loading…";
    private const int NarrowWidth = 1100;
    private static readonly int ProbeRowHeight = Theme.Body.Height + Theme.MonoSmall.Height + 14;
    private static readonly int DrawerRowHeight = 8 + Theme.Body.Height + 6 + 4 + 6 + Theme.MonoSmall.Height + 10;

    private readonly DashboardLoader _loader;
    private readonly PipeClient _pipe;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _persist;
    private readonly ToolTip _tips = new() { InitialDelay = 300 };

    // Header band
    private readonly FlowLayoutPanel _volumeChips = new() { AutoSize = true, WrapContents = false, Margin = new Padding(12, 13, 0, 0), BackColor = Theme.Band };
    private readonly Label _probeInfo = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body, Margin = new Padding(18, 20, 14, 0), BackColor = Theme.Band };
    private readonly ToggleButton _probeNow = new("Probe now", ToggleLook.Primary) { Margin = new Padding(0, 13, 0, 0), AccessibleName = "Measure every Probe now" };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body, Margin = new Padding(8, 20, 0, 0), BackColor = Theme.Band };
    private readonly Panel _statusDot = new() { Size = new Size(12, 12), Margin = new Padding(0, 24, 0, 0), BackColor = Theme.Band };

    // Summary
    private readonly HeroCard _hero = new() { Dock = DockStyle.Fill };
    private readonly StatTile _changeTile = new("Change") { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0) };
    private readonly StatTile _lowestTile = new("Lowest") { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0) };
    private readonly StatTile _forecastTile = new("Full in") { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0) };
    private readonly StatTile _swingsTile = new("Swings") { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 0, 0) };
    private TableLayoutPanel? _summary;
    private readonly List<ToggleButton> _rangeButtons = [];

    // Chart
    private readonly FormsPlot _plot = new() { Dock = DockStyle.Fill, BackColor = Theme.Card };
    private readonly TipBox _tip = new();
    private readonly RichLine _chartHint = new() { Dock = DockStyle.Fill, Margin = new Padding(14, 0, 8, 0) };
    private readonly FlowLayoutPanel _lane = new() { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Card, Padding = new Padding(0, 7, 0, 0) };
    private readonly Label _laneEmpty = new() { AutoSize = true, ForeColor = Theme.Faint, Font = Theme.Small, Margin = new Padding(0, 6, 0, 0), Text = "No Swings in this range" };
    private (double From, double To) _tickSpan;
    private ScottPlot.TickGenerators.NumericAutomatic? _yTicks;
    private ScottPlot.Plottables.VerticalLine? _hoverLine;
    private ScottPlot.Plottables.HorizontalSpan? _dragSpan;

    // Tabs
    private readonly ToggleButton _spaceTab = new("Where space is", ToggleLook.Tab);
    private readonly ToggleButton _swingsTab = new("Swings", ToggleLook.Tab);
    private readonly ToggleButton _writersTab = new("Top Writers", ToggleLook.Tab);
    private readonly Panel _spacePage = new() { Dock = DockStyle.Fill, BackColor = Theme.Card };
    private readonly Panel _swingsPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Card };
    private readonly Panel _writersPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Card };
    private readonly RichLine _spaceIntro = new() { Dock = DockStyle.Fill };
    private readonly ProbeTiles _tiles = new() { Dock = DockStyle.Fill };
    private readonly Panel _tilesHolder = new() { Dock = DockStyle.Top, Height = ProbeTiles.Wanted + 6, Padding = new Padding(20, 2, 20, 4), BackColor = Theme.Card };
    private readonly DarkListView _probeList = new(rowHeight: ProbeRowHeight) { Dock = DockStyle.Fill };
    private readonly ContextMenuStrip _probeMenu = new();
    private readonly DarkListView _swingList = new() { Dock = DockStyle.Fill };
    private readonly RichLine _writersIntro = new() { Dock = DockStyle.Fill };
    private readonly DarkListView _writerList = new() { Dock = DockStyle.Fill };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Faint, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Ground, Padding = new Padding(20, 0, 0, 0) };

    // Swing drawer
    private readonly Panel _drawer = new() { Visible = false, BackColor = Theme.Card, Padding = new Padding(1, 0, 0, 0) };
    private readonly Panel _drawerHeader = new() { Dock = DockStyle.Top, BackColor = Theme.Card };
    private readonly ToggleButton _drawerClose = new("Close", ToggleLook.Action) { Anchor = AnchorStyles.Top | AnchorStyles.Right, AccessibleName = "Close the Swing drawer (Esc)" };
    private readonly DarkListView _itemList = new(rowHeight: DrawerRowHeight) { Dock = DockStyle.Fill };
    private readonly ToggleButton _openLocation = new("Open location", ToggleLook.Primary) { Dock = DockStyle.Fill, Enabled = false, AccessibleName = "Open the selected item's location in Explorer" };

    private TableLayoutPanel? _root;
    private readonly List<Panel> _introBars = [];
    private bool _wasNarrow;
    private string _layoutNote = "";
    private int _probeBarMax = 170;
    private Color _warningColor = Theme.Amber;
    private CardPanel? _chartCard;
    private string _volume;
    private DashboardRange _range;
    private string _tab;
    private int _loadGeneration;
    private Stopwatch? _renderWatch;
    private DashboardSnapshot? _last;
    private (double From, double To)? _zoom;
    private List<ChartPoint> _points = [];
    private long _hoverIndex = -1;
    private double? _dragFrom;
    private int _dragStartPx;
    private long? _copiedProbe;
    private SwingRow? _drawerSwing;
    private bool _syncingSelection;
    private string _diagnostics = "";
    private readonly ToolTip _footerTip = new() { InitialDelay = 600 };

    public DashboardForm(DashboardLoader loader, PipeClient pipe, string volume, bool persist = true)
    {
        _loader = loader;
        _pipe = pipe;
        _persist = persist;
        var saved = persist ? DashboardSettings.Load() : new DashboardSettings();
        _volume = saved.Volume ?? volume;
        _range = saved.Range;
        _tab = saved.Tab is SpaceTab or SwingsTab or WritersTab ? saved.Tab : SpaceTab;

        Text = "DiskMon";
        Icon = AppIcons.Window;
        MinimumSize = new Size(960, 680);
        Size = new Size(1280, 900);
        StartPosition = FormStartPosition.CenterScreen;
        if (saved.Bounds(MinimumSize) is { } bounds)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        if (saved.Maximized)
            WindowState = FormWindowState.Maximized;
        BackColor = Theme.Ground;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Padding = Padding.Empty;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        BuildLayout();
        StylePlot();
        // Hidden until the first read, so the window never shows an empty axis running from 1899.
        _plot.Visible = false;
        WireChartMouse();

        _timer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await ReloadAsync();
        VisibleChanged += (_, _) => _timer.Enabled = Visible;
        Resize += (_, _) =>
        {
            ApplyProportions();
            PlaceDrawer();
        };
        ResizeEnd += (_, _) => SaveSettings();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwmapi.TryUseDarkTitleBar(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyProportions();
        await ReloadAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            _ = ReloadAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _drawer.Visible)
        {
            CloseDrawer();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _zoom is not null)
        {
            ResetZoom();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ---------------------------------------------------------------- screenshots (--screenshot)

    /// <summary>A dashboard that, once shown, saves screenshots to <paramref name="folder"/> and closes.</summary>
    public static DashboardForm ForScreenshots(DiskMonOptions options, string folder)
    {
        var volume = options.Volumes.Contains(DiskMonOptions.DefaultVolume) ? DiskMonOptions.DefaultVolume : options.Volumes[0];
        var form = new DashboardForm(new DashboardLoader(new ReadOnlyDb(options.DatabasePath), options), new PipeClient(), volume, persist: false);
        form.Shown += async (_, _) =>
        {
            try
            {
                await form.CaptureAsync(folder);
            }
            finally
            {
                form.Close();
            }
        };
        return form;
    }

    /// <summary>
    /// Saves the window at each range and tab at 1280×860, then at the minimum size, 1600×1000 and maximised, as
    /// <c>dashboard-&lt;range&gt;-&lt;tab&gt;-&lt;width&gt;x&lt;height&gt;.png</c>.
    /// </summary>
    private async Task CaptureAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        TopMost = true;
        var normal = new Size(1280, 860);
        var shots = DashboardRanges.All.Select(r => (Range: r, Tab: SpaceTab, Size: normal, Max: false))
            .Append((DashboardRange.Hours24, SwingsTab, normal, false))
            .Append((DashboardRange.Hours24, WritersTab, normal, false))
            .Append((DashboardRange.Hours24, SpaceTab, MinimumSize, false))
            .Append((DashboardRange.Hours24, SwingsTab, MinimumSize, false))
            .Append((DashboardRange.Hours24, SpaceTab, new Size(1600, 1000), false))
            .Append((DashboardRange.Hours24, SpaceTab, Size.Empty, true));
        foreach (var (range, tab, size, max) in shots)
        {
            if (max)
            {
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                WindowState = FormWindowState.Normal;
                // Keep the whole window inside the work area, clear of the taskbar.
                var work = Screen.FromControl(this).WorkingArea;
                Bounds = new Rectangle(work.Location, new Size(Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height)));
            }
            SelectRange(range);
            ShowTab(tab);
            CloseDrawer();
            await ReloadAsync();
            if (tab == SwingsTab && _swingList.Items.Count > 0)
            {
                // Show a Swing in the drawer: the newest attributed one if there is one.
                var pick = _swingList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is SwingRow { Stage: "closed" }) ?? _swingList.Items[0];
                pick.Selected = true;
            }
            await Task.Delay(900);
            Refresh();
            var bounds = Dwmapi.TryGetFrameBounds(Handle, out var frame) ? frame : Bounds;
            using var shot = new Bitmap(bounds.Width, bounds.Height);
            try
            {
                using var g = Graphics.FromImage(shot);
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                DrawToBitmap(shot, new Rectangle(Point.Empty, Size));
            }
            var name = max ? "max" : $"{size.Width}x{size.Height}";
            shot.Save(Path.Combine(folder, $"dashboard-{range.Label()}-{tab}-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(Path.Combine(folder, $"dashboard-{range.Label()}-{tab}-{name}.txt"), DescribeLayout());
        }
    }

    /// <summary>The y range the data needs, and the highest warning line drawn (its label needs room above it).</summary>
    private (double Floor, double Top, double? Warning)? _yFrame;

    /// <summary>
    /// Sets the y limits, leaving room above the highest warning line for its label. The room is in pixels, so this
    /// runs again whenever the plot changes height.
    /// </summary>
    private void ApplyYLimits()
    {
        if (_yFrame is not { } frame)
            return;
        var top = frame.Top;
        if (frame.Warning is { } warning)
        {
            // The data area is the plot minus its axis margins; take the margins from the last render (they don't
            // change with the height) and apply them to the current height, which may be newer than that render.
            // The render rectangles are in control pixels (a 248 px plot reports a 248 px figure).
            var last = _plot.Plot.RenderManager.LastRender;
            var scale = Math.Max(1, _plot.DisplayScale);
            var margins = last.DataRect.Height > 0 ? last.FigureRect.Height - last.DataRect.Height : 36 * scale;
            var dataPx = Math.Max(40, _plot.Height - margins);
            // The 11 pt label, its 2 px lift and a little air.
            var labelPx = 18 * scale;
            // Solve top so the warning sits labelPx below it: (top - warning) / (top - floor) = labelPx / dataPx.
            var share = Math.Min(0.6, labelPx / dataPx);
            var needed = (warning - share * frame.Floor) / (1 - share);
            top = Math.Max(top, needed);
        }
        if (_yTicks is not null)
            _yTicks.IntegerTicksOnly = top - frame.Floor >= 3;
        _plot.Plot.Axes.SetLimitsY(frame.Floor, top);
    }

    /// <summary>Where the main parts sit, in client pixels, so a screenshot run can say exactly what got which size.</summary>
    private string DescribeLayout()
    {
        string Box(Control? c) => c is null ? "-" : $"{RectangleToClient(c.RectangleToScreen(c.ClientRectangle))} visible={c.Visible}";
        var lines = new List<string>
        {
            $"dpi {DeviceDpi} window {Size} client {ClientSize} state {WindowState}",
            $"note {_layoutNote}",
            $"diagnostics {_diagnostics}",
            $"root {Box(_root)} rows [{string.Join(", ", _root?.GetRowHeights() ?? [])}] styles [{string.Join(", ", _root?.RowStyles.Cast<RowStyle>().Select(r => $"{r.SizeType} {r.Height}") ?? [])}]",
            $"summary {Box(_summary)}",
            $"chartCard {Box(_chartCard)}",
            $"lane {Box(_lane)} chips {_lane.Controls.OfType<SwingChip>().Count(c => c.Visible)}",
            $"plot {Box(_plot)} scale {_plot.DisplayScale} figure {_plot.Plot.RenderManager.LastRender.FigureRect} data {_plot.Plot.RenderManager.LastRender.DataRect} y {_plot.Plot.Axes.GetLimits().Bottom:0.##}..{_plot.Plot.Axes.GetLimits().Top:0.##}",
            $"tiles {Box(_tilesHolder)}",
            $"probeList {Box(_probeList)}",
            $"swingList {Box(_swingList)}",
            $"drawer {Box(_drawer)} items {_itemList.Items.Count}",
            $"writerList {Box(_writerList)}",
            $"footer {Box(_footer)}",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// Timings and sizes for whoever is tuning the dashboard: on the footer's tooltip and in the screenshot dumps,
    /// not in the footer itself.
    /// </summary>
    private void SetDiagnostics(string text)
    {
        _diagnostics = text;
        _footerTip.SetToolTip(_footer, text);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Ground, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, SummaryHeight + 14));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 400));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildSummary(), 0, 0);
        root.Controls.Add(BuildChartCard(), 0, 1);
        root.Controls.Add(BuildTabsCard(), 0, 2);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 2), BackColor = Theme.Ground };
        body.Controls.Add(root);
        BuildDrawer();

        // Docking runs from the last control added: footer at the bottom, the band on top, the body in between.
        Controls.Add(body);
        Controls.Add(BuildHeader());
        Controls.Add(_footer);
        Controls.Add(_drawer);
        _drawer.BringToFront();
        root.SizeChanged += (_, _) => ApplyProportions();
    }

    /// <summary>
    /// Splits the height between the chart and the tabs (about 55/45, never squeezing either below a usable size),
    /// drops the header's Probe line in a narrow window and the Probe tiles in a short one. Skipped while minimised.
    /// </summary>
    private void ApplyProportions()
    {
        if (_root is null || _chartCard is null || _summary is null || WindowState == FormWindowState.Minimized || ClientSize.Height <= 0)
            return;
        var narrow = ClientSize.Width < NarrowWidth;
        _probeInfo.Visible = !narrow;
        foreach (var intro in _introBars)
            intro.Visible = !narrow;
        _summary.ColumnStyles[0].Width = narrow ? 300 : 380;
        if (narrow != _wasNarrow && _last is not null)
        {
            _wasNarrow = narrow;
            ShowFigures(_last);
            ShowProbes(_last);
        }

        var available = _root.ClientSize.Height - (SummaryHeight + 14);
        var barMax = narrow ? 110 : 170;
        if (_probeList.Columns.Count == 4)
        {
            _probeList.Columns[1].Tag = -(barMax + 10 + TextRenderer.MeasureText("000.0 GB", Theme.Mono).Width + 24);
            _probeList.FillLastColumn();
        }
        _probeBarMax = barMax;
        // Room for the tab strip, column headers and at least two whole Probe rows.
        var tabsMin = Math.Max(narrow ? 0 : 230, 45 + 32 + (int)(2.2 * ProbeRowHeight) + 8);
        const int plotMin = 150;
        var chartMin = _chartCard.Padding.Vertical + ChartTopHeight + LaneHeight + plotMin + 12;
        var chart = (int)(available * (narrow ? 0.58 : 0.55));
        chart = Math.Max(chartMin, Math.Min(chart, available - tabsMin));
        var height = Math.Max(chartMin, chart);
        // The tiles need their two rows plus two Probe rows under them; a short window keeps the list alone.
        var tabs = available - height;
        _tilesHolder.Visible = tabs - 45 - (narrow ? 0 : 38) - _tilesHolder.Height >= 32 + (2 * ProbeRowHeight);
        _layoutNote = $"layout {available}: chart {height}, tabs {tabs}, tiles {_tilesHolder.Visible}";
        if (Math.Abs(_root.RowStyles[1].Height - height) > 0.5f)
        {
            _root.RowStyles[1].Height = height;
            // This often runs from inside a layout pass (a resize), where the table ignores the new row height until
            // something lays it out again: do that once the current pass has finished.
            if (_root.IsHandleCreated)
                _root.BeginInvoke(() => _root.PerformLayout());
        }
        UpdateTickDensity();
        FitLane();
    }

    /// <summary>The header band: logo and name, Volume tabs, then Collector status, the last Probe run and Probe now.</summary>
    private Control BuildHeader()
    {
        var band = new TableLayoutPanel { Dock = DockStyle.Top, Height = HeaderHeight, ColumnCount = 6, RowCount = 1, BackColor = Theme.Band, Margin = Padding.Empty, Padding = new Padding(24, 0, 24, 1) };
        band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++)
            band.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        band.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Hairline);
            e.Graphics.DrawLine(pen, 0, band.Height - 1, band.Width, band.Height - 1);
        };

        var title = new Label { Text = "DiskMon", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Band, Margin = new Padding(0, 18, 0, 0) };
        // The logo beside the name, sized in device pixels so it stays crisp at any DPI (re-made on a DPI change).
        var logo = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Band, Margin = new Padding(0, 15, 10, 0) };
        void SizeLogo()
        {
            var px = LogicalToDeviceUnits(28);
            var old = logo.Image;
            logo.Image = AppIcons.Logo(px);
            logo.Size = new Size(px, px);
            old?.Dispose();
        }
        SizeLogo();
        DpiChanged += (_, _) => SizeLogo();
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Band, Margin = new Padding(0, 0, 8, 0) };
        brand.Controls.Add(logo);
        brand.Controls.Add(title);
        _probeNow.Click += async (_, _) => await ProbeNowAsync();
        _statusDot.Paint += (_, e) =>
        {
            // A dot with a soft halo, lime while the Collector runs, red when it doesn't.
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(Theme.Band);
            using var halo = new SolidBrush(Tint.Over(_statusDot.ForeColor, Theme.Band, 0.22));
            e.Graphics.FillEllipse(halo, 0, 0, 11, 11);
            using var brush = new SolidBrush(_statusDot.ForeColor);
            e.Graphics.FillEllipse(brush, 2, 2, 7, 7);
        };
        var dotAndStatus = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Band, Margin = Padding.Empty };
        dotAndStatus.Controls.Add(_statusDot);
        dotAndStatus.Controls.Add(_status);

        band.Controls.Add(brand, 0, 0);
        band.Controls.Add(_volumeChips, 1, 0);
        band.Controls.Add(dotAndStatus, 3, 0);
        band.Controls.Add(_probeInfo, 4, 0);
        band.Controls.Add(_probeNow, 5, 0);
        return band;
    }

    /// <summary>The summary row: the gauge card, then four equal tiles.</summary>
    private Control BuildSummary()
    {
        var row = _summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Theme.Ground, Margin = new Padding(0, 0, 0, 14), Padding = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        for (var i = 0; i < 4; i++)
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _hero.Margin = Padding.Empty;
        row.Controls.Add(_hero, 0, 0);
        row.Controls.Add(_changeTile, 1, 0);
        row.Controls.Add(_lowestTile, 2, 0);
        row.Controls.Add(_forecastTile, 3, 0);
        row.Controls.Add(_swingsTile, 4, 0);
        _hero.Set("FREE", "—", Theme.Text, "", null);
        _forecastTile.Set("Full in", "—", "needs 6 h of history", Theme.Muted);
        _swingsTile.Set("Swings", "—", "");
        _tips.SetToolTip(_swingsTile, "Click to list them");
        _swingsTile.Cursor = Cursors.Hand;
        _swingsTile.Click += (_, _) => { ShowTab(SwingsTab); SaveSettings(); };
        return row;
    }

    private const int ChartTopHeight = 40;
    private const int LaneHeight = 44;

    private Control BuildChartCard()
    {
        var card = _chartCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 10, 20, 8), Margin = new Padding(0, 0, 0, 14) };

        // Top: the title, a hint (or the zoom note) and the range pills.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = ChartTopHeight, ColumnCount = 3, RowCount = 1, BackColor = Theme.Card, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = "Free space", AutoSize = true, Font = Theme.Section, ForeColor = Theme.Text, BackColor = Theme.Card, Margin = new Padding(0, 9, 0, 0) };
        top.Controls.Add(title, 0, 0);
        top.Controls.Add(_chartHint, 1, 0);
        var ranges = BuildRangeButtons();
        ranges.Margin = new Padding(0, 4, 0, 0);
        top.Controls.Add(ranges, 2, 0);

        // Bottom: the Swing lane, one chip per Swing in the range, newest first.
        var lane = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = LaneHeight, ColumnCount = 2, RowCount = 1, BackColor = Theme.Card, Margin = Padding.Empty };
        lane.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        lane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var laneCaption = new Label { Text = "SWINGS", AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Faint, BackColor = Theme.Card, Margin = new Padding(0, 16, 0, 0) };
        lane.Controls.Add(laneCaption, 0, 0);
        lane.Controls.Add(_lane, 1, 0);
        _lane.Margin = Padding.Empty;
        _lane.Controls.Add(_laneEmpty);
        _lane.Resize += (_, _) => FitLane();

        var gap = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Card };
        _plot.Controls.Add(_tip);
        card.Controls.Add(_plot);
        card.Controls.Add(lane);
        card.Controls.Add(gap);
        card.Controls.Add(top);
        return card;
    }

    private Control BuildRangeButtons()
    {
        var group = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, BackColor = Theme.Card, Padding = Padding.Empty,
            Margin = Padding.Empty, AccessibleName = "Range", AccessibleRole = AccessibleRole.Grouping,
        };
        foreach (var range in DashboardRanges.All)
        {
            var button = new ToggleButton(range.Label(), ToggleLook.Segment) { Tag = range, Selected = range == _range, AccessibleName = $"Show {range.Description()}" };
            button.Click += async (_, _) =>
            {
                SelectRange(range);
                SaveSettings();
                await ReloadAsync();
            };
            _rangeButtons.Add(button);
            group.Controls.Add(button);
        }
        return group;
    }

    private void SelectRange(DashboardRange range)
    {
        if (_range != range)
            _zoom = null;
        _range = range;
        foreach (var b in _rangeButtons)
            b.Selected = (DashboardRange)b.Tag! == range;
    }

    private Control BuildTabsCard()
    {
        var card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty };

        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(10, 2, 0, 0), BackColor = Theme.Card, WrapContents = false };
        strip.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Hairline);
            e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1);
        };
        strip.Controls.Add(_spaceTab);
        strip.Controls.Add(_swingsTab);
        strip.Controls.Add(_writersTab);
        _spaceTab.Click += (_, _) => { ShowTab(SpaceTab); SaveSettings(); };
        _swingsTab.Click += (_, _) => { ShowTab(SwingsTab); SaveSettings(); };
        _writersTab.Click += (_, _) => { ShowTab(WritersTab); SaveSettings(); };

        BuildSpacePage();
        BuildSwingsPage();
        BuildWritersPage();

        var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        pages.Controls.Add(_spacePage);
        pages.Controls.Add(_swingsPage);
        pages.Controls.Add(_writersPage);
        card.Controls.Add(pages);
        card.Controls.Add(strip);
        ShowTab(_tab);
        return card;
    }

    private void BuildSpacePage()
    {
        // Size and Change keep a fixed width (bar + figure); the name and the reclaim hint share what's left.
        var sizeColumn = 170 + 10 + TextRenderer.MeasureText("000.0 GB", Theme.Mono).Width + 24;
        var changeColumn = TextRenderer.MeasureText("−00.0 GB", Theme.Mono).Width + 40;
        AddColumns(_probeList, ("Probe", 40, HorizontalAlignment.Left), ("Size", -sizeColumn, HorizontalAlignment.Left),
            ("Change", -changeColumn, HorizontalAlignment.Right), ("How to reclaim", 60, HorizontalAlignment.Left));
        _probeList.CellPainter = PaintProbeCell;
        _probeList.ShowItemToolTips = true;
        _probeList.EmptyText = Loading;
        _probeList.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                CopyHint(e.Location);
        };
        _probeList.MouseMove += (_, e) =>
        {
            var hit = _probeList.HitTest(e.Location);
            _probeList.Cursor = hit.Item?.Tag is ProbeTag { Hint.Command: not null } && hit.Item.SubItems.IndexOf(hit.SubItem) == 3 ? Cursors.Hand : Cursors.Default;
        };
        _probeList.SelectedIndexChanged += (_, _) => _tiles.SelectedId = SelectedProbe()?.Id;
        _probeList.DoubleClick += (_, _) => OpenPath(SelectedProbe()?.Path);
        _probeList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                OpenPath(SelectedProbe()?.Path);
        };
        BuildProbeMenu();
        _probeList.ContextMenuStrip = _probeMenu;

        _tiles.TileClicked += id =>
        {
            var item = _probeList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is ProbeTag t && t.Probe.Id == id);
            if (item is null)
                return;
            item.Selected = true;
            item.Focused = true;
            _probeList.EnsureVisible(item.Index);
            _probeList.Focus();
        };
        _tiles.Cursor = Cursors.Default;
        _tilesHolder.Controls.Add(_tiles);

        _spacePage.Controls.Add(_probeList);
        _spacePage.Controls.Add(_tilesHolder);
        _spacePage.Controls.Add(IntroBar(_spaceIntro));
    }

    /// <summary>Right-click on a Probe: open its location, copy its path, copy its reclaim command.</summary>
    private void BuildProbeMenu()
    {
        _probeMenu.BackColor = Theme.Raised;
        _probeMenu.ForeColor = Theme.Text;
        _probeMenu.ShowImageMargin = false;
        _probeMenu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        var open = new ToolStripMenuItem("Open location") { ForeColor = Theme.Text };
        var copyPath = new ToolStripMenuItem("Copy path") { ForeColor = Theme.Text };
        var copyCommand = new ToolStripMenuItem("Copy reclaim command") { ForeColor = Theme.Text };
        open.Click += (_, _) => OpenPath(SelectedProbe()?.Path);
        copyPath.Click += (_, _) => CopyText(SelectedProbe()?.Path);
        copyCommand.Click += (_, _) =>
        {
            if (_probeList.SelectedItems.Count > 0 && _probeList.SelectedItems[0].Tag is ProbeTag { Hint.Command: { } command } tag)
                CopyCommand(tag, command);
        };
        _probeMenu.Items.AddRange([open, copyPath, copyCommand]);
        _probeMenu.Opening += (_, e) =>
        {
            // Right-click selects the row under the cursor first.
            var hit = _probeList.HitTest(_probeList.PointToClient(System.Windows.Forms.Cursor.Position));
            if (hit.Item is not null)
                hit.Item.Selected = true;
            if (SelectedProbe() is null)
            {
                e.Cancel = true;
                return;
            }
            copyCommand.Visible = _probeList.SelectedItems[0].Tag is ProbeTag { Hint.Command: not null };
        };
    }

    private ProbeSize? SelectedProbe() =>
        _probeList.SelectedItems.Count > 0 && _probeList.SelectedItems[0].Tag is ProbeTag tag ? tag.Probe : null;

    private void BuildSwingsPage()
    {
        AddColumns(_swingList, ("Time", 20, HorizontalAlignment.Left), ("Volume", 10, HorizontalAlignment.Left),
            ("Kind", 14, HorizontalAlignment.Left), ("Free space", 14, HorizontalAlignment.Right),
            ("Attributed", 14, HorizontalAlignment.Right), ("Unaccounted", 15, HorizontalAlignment.Right),
            ("Stage", 13, HorizontalAlignment.Left));
        _swingList.MonoColumns.UnionWith([0, 3, 4, 5]);
        _swingList.CellPainter = PaintStageCell;
        _swingList.EmptyText = Loading;
        _swingList.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingSelection && _swingList.SelectedItems.Count > 0 && _swingList.SelectedItems[0].Tag is SwingRow swing)
                OpenDrawer(swing);
        };
        // A click on the row that is already selected opens its drawer again after a close.
        _swingList.MouseClick += (_, e) =>
        {
            if (_swingList.HitTest(e.Location).Item?.Tag is SwingRow swing && _drawerSwing?.Id != swing.Id)
                OpenDrawer(swing);
            else if (_swingList.HitTest(e.Location).Item?.Tag is SwingRow again && !_drawer.Visible)
                OpenDrawer(again);
        };
        _swingList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _swingList.SelectedItems.Count > 0 && _swingList.SelectedItems[0].Tag is SwingRow swing)
                OpenDrawer(swing);
        };
        _swingsPage.Controls.Add(_swingList);
    }

    private void BuildWritersPage()
    {
        AddColumns(_writerList, ("#", 6, HorizontalAlignment.Left), ("Program", 26, HorizontalAlignment.Left),
            ("Written", 30, HorizontalAlignment.Left), ("Path", 38, HorizontalAlignment.Left));
        _writerList.MonoColumns.UnionWith([0, 3]);
        _writerList.PathColumns.Add(3);
        _writerList.CellPainter = PaintWriterCell;
        _writerList.ShowItemToolTips = true;
        _writerList.EmptyText = Loading;
        _writersPage.Controls.Add(_writerList);
        _writersPage.Controls.Add(IntroBar(_writersIntro));
    }

    /// <summary>A one-line explanation strip above a tab's list, with a hairline under it.</summary>
    private Panel IntroBar(RichLine line)
    {
        var intro = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(20, 0, 20, 1), BackColor = Theme.Card };
        intro.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Hairline);
            e.Graphics.DrawLine(pen, 0, intro.Height - 1, intro.Width, intro.Height - 1);
        };
        intro.Controls.Add(line);
        _introBars.Add(intro);
        return intro;
    }

    private void ShowTab(string tab)
    {
        _tab = tab;
        _spaceTab.Selected = tab == SpaceTab;
        _swingsTab.Selected = tab == SwingsTab;
        _writersTab.Selected = tab == WritersTab;
        _spacePage.Visible = tab == SpaceTab;
        _swingsPage.Visible = tab == SwingsTab;
        _writersPage.Visible = tab == WritersTab;
        var list = tab switch { SpaceTab => _probeList, SwingsTab => _swingList, _ => _writerList };
        list.FillLastColumn();
        list.UpdateEmpty();
        if (IsHandleCreated)
            ApplyProportions();
    }

    private static void AddColumns(DarkListView list, params (string Text, int Share, HorizontalAlignment Align)[] columns)
    {
        foreach (var (text, share, align) in columns)
            list.Columns.Add(new ColumnHeader { Text = text, Width = 100, TextAlign = align, Tag = share });
        list.ProportionalColumns = true;
    }

    private void SaveSettings()
    {
        if (!_persist || WindowState == FormWindowState.Minimized && RestoreBounds.IsEmpty)
            return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        new DashboardSettings(_volume, _range, _tab, bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized).Save();
    }

    // ---------------------------------------------------------------- chart setup and mouse

    private void StylePlot()
    {
        var plot = _plot.Plot;
        // Swap in the date-time axis first: it replaces the bottom axis, which would drop any colours set before it.
        plot.Axes.DateTimeTicksBottom();
        _yTicks = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => v.ToString(Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB",
        };
        plot.Axes.Left.TickGenerator = _yTicks;
        plot.FigureBackground.Color = Sp(Theme.Card);
        plot.DataBackground.Color = Sp(Theme.PlotGround);
        plot.Grid.MajorLineColor = Sp(Theme.Grid);
        plot.Grid.MajorLinePattern = LinePattern.Dashed;
        plot.Grid.XAxisStyle.IsVisible = false;
        plot.Font.Set(Theme.Body.FontFamily.Name);
        plot.Axes.Color(Sp(Theme.Muted));
        plot.Axes.FrameColor(Sp(Theme.Card));
        foreach (var axis in new ScottPlot.IAxis[] { plot.Axes.Left, plot.Axes.Bottom })
        {
            axis.TickLabelStyle.ForeColor = Sp(Theme.Muted);
            axis.TickLabelStyle.FontSize = 12;
            axis.MajorTickStyle.Color = Sp(Theme.Card);
            axis.MinorTickStyle.Color = Sp(Theme.Card);
        }
        plot.RenderManager.RenderFinished += (_, _) =>
        {
            if (_renderWatch is not { } watch)
                return;
            _renderWatch = null;
            var rendered = watch.Elapsed;
            BeginInvoke(() => SetDiagnostics(_diagnostics + $" · chart drawn in {rendered.TotalMilliseconds:0} ms"));
        };
        _plot.Resize += (_, _) =>
        {
            UpdateTickDensity();
            // The warning label's headroom is in pixels, so a new height needs new y limits.
            ApplyYLimits();
            _plot.Refresh();
        };
    }

    private static readonly (ScottPlot.TickGenerators.ITimeUnit Unit, int Count, double Days)[] TickSteps =
    [
        (new ScottPlot.TickGenerators.TimeUnits.Minute(), 15, 15 / 1440.0),
        (new ScottPlot.TickGenerators.TimeUnits.Minute(), 30, 30 / 1440.0),
        (new ScottPlot.TickGenerators.TimeUnits.Hour(), 1, 1 / 24.0),
        (new ScottPlot.TickGenerators.TimeUnits.Hour(), 2, 2 / 24.0),
        (new ScottPlot.TickGenerators.TimeUnits.Hour(), 3, 3 / 24.0),
        (new ScottPlot.TickGenerators.TimeUnits.Hour(), 6, 6 / 24.0),
        (new ScottPlot.TickGenerators.TimeUnits.Hour(), 12, 0.5),
        (new ScottPlot.TickGenerators.TimeUnits.Day(), 1, 1),
        (new ScottPlot.TickGenerators.TimeUnits.Day(), 2, 2),
        (new ScottPlot.TickGenerators.TimeUnits.Day(), 7, 7),
    ];

    /// <summary>
    /// Picks the finest whole step (15 min … 7 days) that leaves at least about 110 px per label, so 7 days shows
    /// days and a maximised window gets more labels. Labels: the day at midnight, otherwise the time.
    /// </summary>
    private void UpdateTickDensity()
    {
        var (from, to) = _tickSpan;
        if (to <= from || _plot.Width <= 0)
            return;
        var maxTicks = Math.Max(3, _plot.Width / 110);
        var step = TickSteps.FirstOrDefault(t => (to - from) / t.Days <= maxTicks);
        if (step.Unit is null)
            step = TickSteps[^1];
        var daily = step.Days >= 1;
        var dateLabels = daily && step.Days > 1;
        var stepMinutes = (int)Math.Round(step.Days * 1440);
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.DateTimeFixedInterval(step.Unit, step.Count)
        {
            // Start counting from a whole step (midnight, a whole hour, :00/:15/:30), not from the range's first instant.
            GetIntervalStartFunc = d => stepMinutes >= 1440
                ? d.Date
                : d.Date.AddMinutes(Math.Floor(d.TimeOfDay.TotalMinutes / stepMinutes) * stepMinutes),
            LabelFormatter = d => d.ToString(dateLabels ? "d MMM" : d.TimeOfDay == TimeSpan.Zero ? "ddd d" : "HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Hover shows the nearest sample; dragging across selects a time span to zoom into; double-click resets.</summary>
    private void WireChartMouse()
    {
        _plot.UserInputProcessor.Disable();
        _plot.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _last is null)
                return;
            _dragFrom = Coordinates(e.Location).X;
            _dragStartPx = e.X;
        };
        _plot.MouseMove += (_, e) =>
        {
            if (_dragFrom is { } from && e.Button == MouseButtons.Left)
            {
                if (Math.Abs(e.X - _dragStartPx) < 5 || _dragSpan is null)
                    return;
                HideHover();
                var x = Coordinates(e.Location).X;
                _dragSpan.X1 = Math.Min(from, x);
                _dragSpan.X2 = Math.Max(from, x);
                _dragSpan.IsVisible = true;
                _plot.Refresh();
                return;
            }
            ShowHover(e.Location);
        };
        _plot.MouseUp += (_, e) =>
        {
            if (_dragFrom is not { } from)
                return;
            _dragFrom = null;
            if (_dragSpan is not null)
                _dragSpan.IsVisible = false;
            var to = Coordinates(e.Location).X;
            if (Math.Abs(e.X - _dragStartPx) >= 12 && _last is not null)
            {
                _zoom = (Math.Min(from, to), Math.Max(from, to));
                ShowChart(_last);
            }
            else if (SwingAt(e.X) is { } swing)
            {
                _plot.Refresh();
                OpenDrawer(swing);
            }
            else
            {
                _plot.Refresh();
            }
        };
        _plot.MouseLeave += (_, _) =>
        {
            HideHover();
            _plot.Refresh();
        };
        _plot.DoubleClick += (_, _) => ResetZoom();
    }

    private void ResetZoom()
    {
        if (_zoom is null || _last is null)
            return;
        _zoom = null;
        ShowChart(_last);
    }

    /// <summary>
    /// The Swing whose span is under pixel column <paramref name="x"/>, with a few pixels' slack so a Swing of a few
    /// minutes (a sliver on a 24 h chart) can still be hit; the narrowest wins where spans overlap.
    /// </summary>
    private SwingRow? SwingAt(int x)
    {
        if (_last is not { } s)
            return null;
        var slack = 4 * _plot.DisplayScale;
        double Px(long ts) => _plot.Plot.GetPixel(new Coordinates(OaDate(ts), 0)).X * _plot.DisplayScale;
        return s.Swings
            .Select(w => (Swing: w, From: Px(Math.Max(w.TsStart, s.FromTs)), To: Px(Math.Min(w.TsEnd ?? s.ToTs, s.ToTs))))
            .Where(w => x >= w.From - slack && x <= w.To + slack)
            .OrderBy(w => w.To - w.From)
            .Select(w => w.Swing)
            .FirstOrDefault();
    }

    private Coordinates Coordinates(Point location) =>
        _plot.Plot.GetCoordinates(new Pixel(location.X / _plot.DisplayScale, location.Y / _plot.DisplayScale));

    private void ShowHover(Point location)
    {
        if (_points.Count == 0 || _hoverLine is null)
            return;
        var swing = SwingAt(location.X);
        _plot.Cursor = swing is null ? Cursors.Cross : Cursors.Hand;
        var x = Coordinates(location).X;
        var i = Nearest(x);
        var px = _plot.Plot.GetPixel(new Coordinates(OaDate(_points[i].Ts), 0)).X * _plot.DisplayScale;
        if (Math.Abs(px - location.X) > 40)
        {
            HideHover();
            _plot.Refresh();
            return;
        }
        var hoverKey = (i * 1000003L) + (swing?.Id ?? 0);
        if (hoverKey == _hoverIndex)
            return;
        _hoverIndex = hoverKey;
        var p = _points[i];
        _hoverLine.X = OaDate(p.Ts);
        _hoverLine.IsVisible = true;
        var sub = swing is null ? "" : $"{KindName(swing)} Swing {ShortTime(swing.TsStart)} · click to open";
        _tip.Set(DateTimeOffset.FromUnixTimeMilliseconds(p.Ts).LocalDateTime.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), $"{SizeText.Gb(p.FreeBytes)} free", sub);
        var left = (int)px + 12;
        if (left + _tip.Width > _plot.Width - 8)
            left = (int)px - 12 - _tip.Width;
        _tip.Location = new Point(Math.Max(4, left), 10);
        _tip.Visible = true;
        _tip.BringToFront();
        _plot.Refresh();
    }

    private void HideHover()
    {
        _hoverIndex = -1;
        _tip.Visible = false;
        if (_hoverLine is not null)
            _hoverLine.IsVisible = false;
    }

    private int Nearest(double oaDate)
    {
        int lo = 0, hi = _points.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (OaDate(_points[mid].Ts) < oaDate)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo > 0 && Math.Abs(OaDate(_points[lo - 1].Ts) - oaDate) < Math.Abs(OaDate(_points[lo].Ts) - oaDate))
            lo--;
        return lo;
    }

    // ---------------------------------------------------------------- data

    private async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        var volume = _volume;
        var range = _range;
        var total = Stopwatch.StartNew();
        var ping = Task.Run(PingAsync);
        DashboardSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => _loader.Load(volume, range, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (SqliteException ex)
        {
            if (generation == _loadGeneration)
                ShowError(ex);
            return;
        }

        if (generation != _loadGeneration || IsDisposed)
            return;

        if (!snapshot.Volumes.Contains(volume, StringComparer.OrdinalIgnoreCase) && snapshot.Volumes.Count > 0)
        {
            // A saved Volume that no longer exists: fall back to the first one.
            _volume = snapshot.Volumes[0];
            await ReloadAsync();
            return;
        }

        if (_last is null)
        {
            // First data: the chart was hidden and the lists said "Loading…" until now.
            _plot.Visible = true;
            _probeList.EmptyText = "No Probe runs yet. Use Probe now, or wait for the Collector's next run (every 10 minutes).";
            _swingList.EmptyText = "No Swings in this range. A Swing is recorded when free space moves by 1 GB or more within 15 minutes.";
            _writerList.EmptyText = "No Top Writers recorded in this range.";
        }
        _last = snapshot;
        ShowVolumes(snapshot.Volumes);
        ShowFigures(snapshot);
        _renderWatch = total;
        ShowChart(snapshot);
        ShowProbes(snapshot);
        ShowSwings(snapshot);
        ShowWriters(snapshot);
        ShowStatus(await ping, snapshot);
    }

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

    /// <summary>Asks the Collector to measure every Probe now, then reloads to show the new sizes.</summary>
    private async Task ProbeNowAsync()
    {
        if (!_probeNow.Enabled)
            return;
        _probeNow.Enabled = false;
        _probeNow.Text = "Probing…";
        try
        {
            var response = await Task.Run(() => _pipe.ProbeNowAsync());
            _footer.Text = response.Ok
                ? $"Probe run finished: {response.Probes} Probes in {(response.DurationMs ?? 0) / 1000.0:0.#} s" + (response.Errors is > 0 ? $", {response.Errors} with errors" : "")
                : $"Probe run failed: {response.Error}";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _footer.Text = $"Probe now needs the Collector, which did not answer ({ex.Message})";
        }
        finally
        {
            _probeNow.Text = "Probe now";
            _probeNow.Enabled = true;
        }
        await ReloadAsync();
    }

    private void ShowVolumes(IReadOnlyList<string> volumes)
    {
        var current = _volumeChips.Controls.Cast<ToggleButton>().Select(b => (string)b.Tag!).ToList();
        if (!current.SequenceEqual(volumes))
        {
            _volumeChips.SuspendLayout();
            foreach (Control c in _volumeChips.Controls)
                c.Dispose();
            _volumeChips.Controls.Clear();
            foreach (var volume in volumes)
            {
                var chip = new ToggleButton(VolumeLabel(volume), ToggleLook.Chip) { Tag = volume, AccessibleName = $"Volume {volume}" };
                chip.Click += async (_, _) =>
                {
                    if (!_volume.Equals(volume, StringComparison.OrdinalIgnoreCase))
                        _zoom = null;
                    _volume = volume;
                    foreach (ToggleButton b in _volumeChips.Controls)
                        b.Selected = (string)b.Tag! == volume;
                    SaveSettings();
                    await ReloadAsync();
                };
                _volumeChips.Controls.Add(chip);
            }
            _volumeChips.ResumeLayout();
        }
        foreach (ToggleButton b in _volumeChips.Controls)
            b.Selected = ((string)b.Tag!).Equals(_volume, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>C:  Windows</c> with the drive's own label, <c>C:  System</c> for the Windows drive, else the letter.</summary>
    private static string VolumeLabel(string volume)
    {
        try
        {
            var label = new DriveInfo(volume).VolumeLabel;
            if (!string.IsNullOrWhiteSpace(label))
                return $"{volume}  {label}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        var system = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        return string.Equals(system, volume, StringComparison.OrdinalIgnoreCase) ? $"{volume}  System" : volume;
    }

    private void ShowFigures(DashboardSnapshot s)
    {
        var narrow = ClientSize.Width < NarrowWidth;
        var changeCaption = s.Summary is { } first && first.FirstTs - s.FromTs > 2 * (long)TimeSpan.FromMinutes(10).TotalMilliseconds
            ? $"Change since {ShortTime(first.FirstTs)}"
            : $"Change over {s.Range.Label()}";
        ShowForecast(s);
        var open = s.Swings.Count(w => w.Stage != "closed");
        _swingsTile.Set("Swings", s.Swings.Count.ToString(CultureInfo.InvariantCulture),
            s.Swings.Count == 0 ? "none in this range" : open > 0 ? $"{open} open" : "all attributed");
        if (s.Summary is not { } sum)
        {
            _hero.Set($"FREE ON {s.Volume}", "—", Theme.Text, "no samples in this range", null);
            _changeTile.Set(changeCaption, "—", "");
            _lowestTile.Set("Lowest", "—", "");
            return;
        }

        var level = FreeSpaceStatus.Level(sum.FreeBytes, sum.TotalBytes, _loader.TrayOptions);
        var (amber, red) = FreeSpaceStatus.WarningLevels(sum.TotalBytes, _loader.TrayOptions);
        var of = sum.TotalBytes > 0 ? $"of {SizeText.Gb(sum.TotalBytes)}" : "size unknown";
        var detail = level switch
        {
            FreeSpaceLevel.Red => narrow ? $"{of} · under red" : $"{of} · under the red level ({SizeText.Gb(red)})",
            FreeSpaceLevel.Amber => narrow ? $"{of} · under amber" : $"{of} · under the amber level ({SizeText.Gb(amber)})",
            _ => sum.FreePercent is { } pct ? $"{pct.ToString("0.0", CultureInfo.InvariantCulture)}% free {of}" : of,
        };
        var color = level switch { FreeSpaceLevel.Red => Theme.Red, FreeSpaceLevel.Amber => Theme.Amber, _ => Theme.Text };
        _hero.Set($"FREE ON {s.Volume}", SizeText.Gb(sum.FreeBytes), color, detail,
            sum.TotalBytes > 0 ? 1 - ((double)sum.FreeBytes / sum.TotalBytes) : null);

        // More free space is good news (lime), less is growth (pink), matching the chart's Swing colours.
        var changeColor = Math.Abs(sum.ChangeBytes) < Bytes.GB / 20 ? Theme.Text : sum.ChangeBytes > 0 ? Theme.Lime : Theme.Growth;
        _changeTile.Set(changeCaption, SizeText.SignedGb(sum.ChangeBytes), "in free space", changeColor);
        _lowestTile.Set("Lowest", SizeText.Gb(sum.LowestFreeBytes), $"over {s.Range.Description()}");
    }

    private void ShowForecast(DashboardSnapshot s)
    {
        if (s.Forecast is not { } f)
        {
            _forecastTile.Set("Full in", "—", "needs 6 h of history", Theme.Muted);
            return;
        }
        var basis = f.SpanDays >= 6.5 ? "the 7-day trend" : $"the trend since {ShortTime(s.ForecastFromTs)}";
        var perDay = $"{SizeText.SignedGb((long)f.BytesPerDay)}/day";
        if (f.DaysToFull is not { } days)
        {
            _forecastTile.Set("Full in", "Not filling", "steady or rising", Theme.Lime, Theme.LimeGround, Theme.LimeEdge);
            _tips.SetToolTip(_forecastTile, $"Steady or rising at {basis}");
            return;
        }
        var value = days switch
        {
            < 1 => "< 1 day",
            < 1.5 => "≈ 1 day",
            < 60 => $"≈ {days:0} days",
            < 730 => $"≈ {days / 30:0} months",
            _ => "over 2 years",
        };
        var (accent, ground, edge) = days switch
        {
            < 7 => (Theme.Red, Theme.RedGround, Theme.RedEdge),
            < 30 => (Theme.Amber, Theme.AmberGround, Theme.AmberEdge),
            _ => (Theme.Text, Theme.Card, Theme.Hairline),
        };
        _forecastTile.Set("Full in", value, perDay, accent, ground, edge);
        _tips.SetToolTip(_forecastTile, $"At {basis}, {perDay}");
    }

    private void ShowChart(DashboardSnapshot s)
    {
        var plot = _plot.Plot;
        plot.Clear();
        HideHover();

        foreach (var swing in s.Swings)
        {
            var end = swing.TsEnd ?? s.ToTs;
            var span = plot.Add.HorizontalSpan(OaDate(Math.Max(swing.TsStart, s.FromTs)), OaDate(Math.Min(end, s.ToTs)));
            var color = Sp(swing.Kind == "growth" ? Theme.Growth : Theme.Release);
            span.FillStyle.Color = color.WithAlpha(swing.AcrossDowntime ? 0.10 : 0.18);
            span.LineStyle.Width = 0;
            if (swing.AcrossDowntime)
            {
                span.FillStyle.Hatch = new ScottPlot.Hatches.Striped();
                span.FillStyle.HatchColor = color.WithAlpha(0.35);
            }
        }

        _points = s.Runs.SelectMany(r => r).OrderBy(p => p.Ts).ToList();
        var (fromX, toX) = _zoom ?? (OaDate(s.FromTs), OaDate(s.ToTs));
        var warnings = new List<string>();
        var highestWarning = 0.0;
        if (_points.Count > 0)
        {
            var visible = _points.Where(p => OaDate(p.Ts) >= fromX && OaDate(p.Ts) <= toX).ToList();
            if (visible.Count == 0)
                visible = _points;
            var low = visible.Min(p => p.FreeBytes) / (double)Bytes.GB;
            var high = visible.Max(p => p.FreeBytes) / (double)Bytes.GB;

            // Warning lines at the tray's amber and red levels, drawn when they fall within (or just above) the data,
            // so a far-off level never squashes the line into the bottom of the chart.
            if (s.Summary is { } sum)
            {
                var (amber, red) = FreeSpaceStatus.WarningLevels(sum.TotalBytes, _loader.TrayOptions);
                var reach = Math.Max(1.0, (high - low) * 0.35);
                _warningColor = Theme.Amber;
                foreach (var (bytes, color, name) in new[] { (amber, Theme.Amber, "amber"), (red, Theme.Red, "red") })
                {
                    var gb = (double)bytes / Bytes.GB;
                    if (gb > high + reach || gb < low - reach)
                        continue;
                    if (warnings.Count == 0)
                        _warningColor = color;
                    low = Math.Min(low, gb);
                    high = Math.Max(high, gb);
                    highestWarning = Math.Max(highestWarning, gb);
                    var line = plot.Add.HorizontalLine(gb);
                    line.Color = Sp(color).WithAlpha(0.8);
                    line.LineWidth = 1.5f;
                    line.LinePattern = LinePattern.Dashed;
                    var tag = plot.Add.Text($"{name} · {SizeText.Gb(bytes)}", fromX, gb);
                    tag.LabelFontColor = Sp(color);
                    tag.LabelFontSize = 11;
                    tag.LabelBold = true;
                    tag.LabelAlignment = Alignment.LowerLeft;
                    tag.LabelOffsetX = 6;
                    tag.LabelOffsetY = -2;
                    warnings.Add(name);
                }
            }

            var pad = Math.Max(0.5, (high - low) * 0.12);
            var floor = Math.Max(0, low - pad);
            // One line per run, so Downtime shows as a break rather than a straight line across the gap. Short runs
            // get a dot instead of a fill, which would draw as a hanging box.
            foreach (var run in s.Runs)
            {
                var line = plot.Add.Scatter(run.Select(p => OaDate(p.Ts)).ToArray(), run.Select(p => (double)p.FreeBytes / Bytes.GB).ToArray());
                line.MarkerSize = run.Count < 3 ? 4 : 0;
                line.MarkerFillColor = Sp(Theme.Line);
                line.LineWidth = 2;
                line.Color = Sp(Theme.Line);
                line.FillY = run.Count >= 3;
                line.FillYColor = Sp(Theme.Fill).WithAlpha(0.28);
                line.FillYValue = floor;
            }
            _yFrame = (floor, high + pad, warnings.Count > 0 ? highestWarning : null);
            ApplyYLimits();
        }
        else
        {
            var empty = plot.Add.Annotation($"No samples for {s.Volume} in {s.Range.Description()}");
            empty.LabelFontColor = Sp(Theme.Muted);
            empty.LabelBackgroundColor = Sp(Theme.PlotGround);
            empty.LabelBorderColor = Sp(Theme.PlotGround);
            empty.Alignment = Alignment.MiddleCenter;
            _yFrame = null;
            plot.Axes.SetLimitsY(0, 1);
        }

        _hoverLine = plot.Add.VerticalLine(fromX);
        _hoverLine.Color = Sp(Theme.Muted);
        _hoverLine.LineWidth = 1;
        _hoverLine.LinePattern = LinePattern.Dotted;
        _hoverLine.IsVisible = false;
        _dragSpan = plot.Add.HorizontalSpan(fromX, fromX);
        _dragSpan.FillStyle.Color = Sp(Theme.SelectedEdge).WithAlpha(0.25);
        _dragSpan.LineStyle.Color = Sp(Theme.SelectedEdge);
        _dragSpan.IsVisible = false;

        plot.Axes.SetLimitsX(fromX, toX);
        _tickSpan = (fromX, toX);
        UpdateTickDensity();
        ShowChartHint(warnings);
        _footer.Text = $"Updated {DateTime.Now:HH:mm:ss}"
            + (s.Mode == ReadOnlyOpenMode.Immutable ? " · read-only snapshot (Collector stopped)" : "")
            + "  ·  F5 refreshes";
        SetDiagnostics($"read in {s.ReadTime.TotalMilliseconds:0} ms · {_points.Count} points · {_layoutNote}");
        _plot.Refresh();
    }

    /// <summary>The line beside the chart's title: how to use the chart, or the zoom note, and the warning-line key.</summary>
    private void ShowChartHint(List<string> warnings)
    {
        var parts = new List<(string, Color, Font)>();
        if (_zoom is not null)
            parts.Add(("Zoomed in: double-click or Esc to reset", Theme.Lime, Theme.BodyStrong));
        else
            parts.Add((ClientSize.Width < NarrowWidth ? "hover · drag to zoom" : "hover for values  ·  drag to zoom  ·  click a Swing to open it", Theme.Faint, Theme.Small));
        if (warnings.Count > 0 && ClientSize.Width >= NarrowWidth)
        {
            parts.Add(("      - - ", _warningColor, Theme.Small));
            parts.Add(("tray warning levels", Theme.Faint, Theme.Small));
        }
        _chartHint.SetParts([.. parts]);
    }

    // ---------------------------------------------------------------- Where space is

    private sealed record ProbeTag(ProbeSize Probe, double Share, ReclaimHint? Hint);

    private void ShowProbes(DashboardSnapshot s)
    {
        var shown = s.Probes.Where(p => p.SizeBytes > 0 || p.ChangeBytes is not null and not 0).ToList();
        var empty = s.Probes.Count - shown.Count;
        var inProbes = shown.Sum(p => p.SizeBytes);
        if (s.Summary is { } sum && shown.Count > 0)
        {
            var used = sum.TotalBytes - sum.FreeBytes;
            _spaceIntro.SetParts(
                ("Probes hold ", Theme.Text, Theme.Body),
                (SizeText.Gb(inProbes), Theme.Text, Theme.BodyStrong),
                ($" of the {SizeText.Gb(used)} used on {s.Volume}", Theme.Text, Theme.Body),
                (ClientSize.Width < NarrowWidth ? "" : $"   ·   change over {s.Range.Description()}   ·   click a command to copy it, right-click a row for more" + (empty > 0 ? $"   ·   {empty} empty Probes hidden" : ""), Theme.Muted, Theme.Small));
        }
        else
        {
            _spaceIntro.SetParts(("Probes measure known space hogs (caches, temp folders, WSL and Docker disks) every 10 minutes.", Theme.Muted, Theme.Body));
        }

        _probeList.BeginUpdate();
        _probeList.Items.Clear();
        var top = shown.Count > 0 ? Math.Max(1, shown[0].SizeBytes) : 1;
        foreach (var p in shown)
        {
            var hint = ReclaimHints.For(p.Name);
            var item = new ListViewItem(p.Name) { Tag = new ProbeTag(p, (double)p.SizeBytes / top, hint) };
            item.SubItems.Add(SizeText.Gb(p.SizeBytes));
            item.SubItems.Add(p.ChangeBytes is { } c ? SignedUsed(c) : "—");
            item.SubItems.Add(hint?.Command ?? hint?.Note ?? "");
            item.ToolTipText = p.Path + (hint is null ? "" : $"\n{hint.Command}{(hint.Command is null ? "" : " — ")}{hint.Note}") + (p.Error is { } err ? $"\n{err} (size may be partial)" : "") + $"\nmeasured {Ago(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.Ts)}";
            _probeList.Items.Add(item);
        }
        _probeList.EndUpdate();
        _probeList.UpdateEmpty();
        _tiles.SetItems(shown.Select(p => (p, p.Name)));
        _tiles.SelectedId = SelectedProbe()?.Id;
        _probeInfo.Text = s.LatestProbeTs is { } ts ? $"Probes {Ago(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts)}" : "no run yet";
        _tips.SetToolTip(_probeNow, _probeInfo.Text);
    }

    /// <summary>A Probe's change in its own terms: growing takes space (pink), shrinking frees it (lime).</summary>
    private static string SignedUsed(long bytes) =>
        Math.Abs(bytes) < Bytes.GB / 20 ? (Math.Abs(bytes) < Bytes.MB ? "±0" : (bytes > 0 ? "+" : "−") + SizeText.Compact(Math.Abs(bytes))) : (bytes > 0 ? "+" : "−") + SizeText.Gb(Math.Abs(bytes));

    private bool PaintProbeCell(Graphics g, Rectangle cell, ListViewItem item, int column)
    {
        if (item.Tag is not ProbeTag tag)
            return false;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        switch (column)
        {
            case 0:
                var nameHeight = Theme.Body.Height;
                var top = cell.Y + Math.Max(2, (cell.Height - nameHeight - Theme.MonoSmall.Height - 2) / 2);
                TextRenderer.DrawText(g, tag.Probe.Name + (tag.Probe.Error is null ? "" : "  ⚠"), Theme.Body, new Rectangle(cell.X, top, cell.Width, nameHeight), tag.Probe.Error is null ? Theme.Text : Theme.Amber, flags);
                TextRenderer.DrawText(g, tag.Probe.Path, Theme.MonoSmall, new Rectangle(cell.X, top + nameHeight + 2, cell.Width, Theme.MonoSmall.Height + 4), Theme.Faint,
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
                return true;
            case 1:
                PaintBar(g, cell, tag.Share, Theme.Fill, item.SubItems[1].Text, _probeBarMax);
                return true;
            case 2:
                var change = tag.Probe.ChangeBytes;
                var color = change is null || Math.Abs(change.Value) < Bytes.MB ? Theme.Muted : change > 0 ? Theme.Growth : Theme.Release;
                TextRenderer.DrawText(g, item.SubItems[2].Text, Theme.Mono, cell, color, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return true;
            case 3:
                if (tag.Hint is null)
                    return true;
                var x = cell.X;
                if (tag.Hint.Command is { } command)
                {
                    var text = _copiedProbe == tag.Probe.Id ? "Copied" : command;
                    var size = TextRenderer.MeasureText(text, Theme.MonoSmall, Size.Empty, TextFormatFlags.NoPadding);
                    var chipHeight = Theme.MonoSmall.Height + 10;
                    var chip = new Rectangle(x, cell.Y + ((cell.Height - chipHeight) / 2), Math.Min(cell.Width, size.Width + 16), chipHeight);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var path = CardPanel.Rounded(chip, 5))
                    {
                        using var fill = new SolidBrush(Theme.Ground);
                        g.FillPath(fill, path);
                        using var pen = new Pen(Theme.Hairline);
                        g.DrawPath(pen, path);
                    }
                    TextRenderer.DrawText(g, text, Theme.MonoSmall, Rectangle.Inflate(chip, -8, 0), _copiedProbe == tag.Probe.Id ? Theme.Lime : Theme.Text, flags | TextFormatFlags.VerticalCenter);
                    x = chip.Right + 10;
                }
                if (tag.Hint.Note.Length > 0 && x < cell.Right - 20)
                    TextRenderer.DrawText(g, tag.Hint.Note, Theme.Small, new Rectangle(x, cell.Y, cell.Right - x, cell.Height), Theme.Faint, flags | TextFormatFlags.VerticalCenter);
                return true;
        }
        return false;
    }

    private void CopyHint(Point location)
    {
        var hit = _probeList.HitTest(location);
        if (hit.Item?.Tag is not ProbeTag { Hint.Command: { } command } tag || hit.Item.SubItems.IndexOf(hit.SubItem) != 3)
            return;
        CopyCommand(tag, command);
    }

    /// <summary>Copies a reclaim command and shows "Copied" in its chip for a moment.</summary>
    private void CopyCommand(ProbeTag tag, string command)
    {
        if (!CopyText(command))
            return;
        _copiedProbe = tag.Probe.Id;
        _probeList.Invalidate();
        var reset = new System.Windows.Forms.Timer { Interval = 1800 };
        reset.Tick += (_, _) =>
        {
            reset.Dispose();
            _copiedProbe = null;
            _probeList.Invalidate();
        };
        reset.Start();
    }

    private bool CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            Clipboard.SetText(text);
            _footer.Text = $"Copied {text}";
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Swings

    private void ShowSwings(DashboardSnapshot s)
    {
        _syncingSelection = true;
        _swingList.BeginUpdate();
        _swingList.Items.Clear();
        foreach (var w in s.Swings)
        {
            var item = new ListViewItem(LocalTime(w.TsStart, s.Range)) { Tag = w };
            item.SubItems.Add(w.Volume);
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, KindName(w) + (w.AcrossDowntime ? " ↯" : ""))
            {
                ForeColor = w.Kind == "growth" ? Theme.Growth : Theme.Release,
            });
            // used_delta is +growth / −release in used space; show the change in free space, matching the chart.
            item.SubItems.Add(w.UsedDeltaBytes is { } used ? SizeText.SignedGb(-used) : "…");
            // Attributed and Unaccounted are changes in used space (what grew), like the drawer's items.
            item.SubItems.Add(w.AttributedBytes is { } a ? SizeText.SignedGb(a) : "—");
            item.SubItems.Add(w.UnaccountedBytes is { } u ? SizeText.SignedGb(u) : "—");
            item.SubItems.Add(w.Stage switch { "open" => "open", "closing" => "attributing…", _ => "closed" });
            item.Selected = w.Id == _drawerSwing?.Id;
            _swingList.Items.Add(item);
        }
        _swingList.EndUpdate();
        _swingList.UpdateEmpty();
        _syncingSelection = false;
        _swingsTab.Text = s.Swings.Count > 0 ? $"Swings · {s.Swings.Count}" : "Swings";
        ShowLane(s);
        // The drawer's Swing may have moved on (Closing → Closed): show its new row, or close it when it left the range.
        if (_drawerSwing is { } shown)
        {
            if (s.Swings.FirstOrDefault(w => w.Id == shown.Id) is { } fresh)
                OpenDrawer(fresh, refresh: true);
            else
                CloseDrawer();
        }
    }

    private static string KindName(SwingRow swing) => swing.Kind == "growth" ? "Growth" : "Release";

    /// <summary>The chart's Swing lane: a chip per Swing, newest first, as many as fit.</summary>
    private void ShowLane(DashboardSnapshot s)
    {
        _lane.SuspendLayout();
        foreach (var chip in _lane.Controls.OfType<SwingChip>().ToList())
        {
            _lane.Controls.Remove(chip);
            chip.Dispose();
        }
        foreach (var w in s.Swings.OrderByDescending(w => w.TsStart))
        {
            var free = w.UsedDeltaBytes is { } used ? SizeText.SignedGb(-used) : "…";
            var chip = new SwingChip(w, $"{ShortTime(w.TsStart)}  {KindName(w)}  {free}") { Selected = w.Id == _drawerSwing?.Id };
            chip.Click += (_, _) => OpenDrawer(chip.Swing);
            _tips.SetToolTip(chip, w.Stage == "closed" ? "Open to see what changed" : "Still settling; open to follow it");
            _lane.Controls.Add(chip);
        }
        _laneEmpty.Visible = s.Swings.Count == 0;
        _lane.ResumeLayout();
        FitLane();
    }

    /// <summary>Hides the chips that would run past the lane's end, so none is drawn cut off.</summary>
    private void FitLane()
    {
        var x = 0;
        var width = _lane.ClientSize.Width;
        foreach (var chip in _lane.Controls.OfType<SwingChip>())
        {
            x += chip.Width + chip.Margin.Horizontal;
            var fits = x <= width;
            if (chip.Visible != fits)
                chip.Visible = fits;
        }
    }

    private static bool PaintStageCell(Graphics g, Rectangle cell, ListViewItem item, int column)
    {
        if (column != 6 || item.Tag is not SwingRow swing)
            return false;
        var text = item.SubItems[6].Text;
        var size = TextRenderer.MeasureText(text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
        var pill = new Rectangle(cell.X, cell.Y + ((cell.Height - 20) / 2), size.Width + 16, 20);
        var (back, fore) = swing.Stage == "closed" ? (Theme.Grid, Theme.Muted) : (Theme.AmberGround, Theme.Amber);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = CardPanel.Rounded(pill, 9))
        using (var fill = new SolidBrush(back))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, text, Theme.Small, pill, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return true;
    }

    /// <summary>Top Writers listed in a Swing's drawer; the Top Writers tab has the full ranking.</summary>
    private const int DetailWritersShown = 5;

    private const string UnaccountedHint = "Hidden system storage (VSS, pagefile) or locations not covered by probes";

    /// <summary>One row of a Swing's item list. <see cref="Share"/> scales its bar against the largest Probe row.</summary>
    private sealed record DetailRow(string Source, long DeltaBytes, string? Path, double Share);

    private int DrawerHeaderHeight => 18 + 26 + 14 + Theme.Hero.Height + Theme.Small.Height + 14 + DrawerFigureHeight + 16;

    private static int DrawerFigureHeight => 10 + Theme.Small.Height + 2 + Theme.Section.Height + 10;

    /// <summary>The Swing drawer: kind and stage, the change, its times, Attributed and Unaccounted, the items, Open location.</summary>
    private void BuildDrawer()
    {
        _drawer.Paint += (_, e) =>
        {
            using var edge = new Pen(Theme.StrongEdge);
            e.Graphics.DrawLine(edge, 0, 0, 0, _drawer.Height);
        };
        _drawerHeader.Height = DrawerHeaderHeight;
        _drawerHeader.Paint += PaintDrawerHeader;
        _drawerHeader.Resize += (_, _) =>
        {
            _drawerClose.Location = new Point(_drawerHeader.ClientSize.Width - 20 - _drawerClose.Width, 14);
            _drawerHeader.Invalidate();
        };
        _drawerClose.Click += (_, _) => CloseDrawer();
        _drawerHeader.Controls.Add(_drawerClose);

        _itemList.Columns.Add(new ColumnHeader { Text = "What changed", Width = 300, Tag = 100 });
        _itemList.ProportionalColumns = true;
        _itemList.HeaderStyle = ColumnHeaderStyle.None;
        _itemList.CellPainter = PaintItemCell;
        _itemList.ShowItemToolTips = true;
        _itemList.SelectedIndexChanged += (_, _) => _openLocation.Enabled = SelectedItemPath() is not null;
        _itemList.DoubleClick += (_, _) => OpenPath(SelectedItemPath());
        _itemList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
                OpenPath(SelectedItemPath());
        };
        _tips.SetToolTip(_openLocation, "Open the selected row's folder in Explorer (or select its file). Double-click a row does the same.");
        _openLocation.Click += (_, _) => OpenPath(SelectedItemPath());

        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0), BackColor = Theme.Card };
        listHolder.Controls.Add(_itemList);
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(20, 12, 20, 16), BackColor = Theme.Card };
        foot.Controls.Add(_openLocation);
        _drawer.Controls.Add(listHolder);
        _drawer.Controls.Add(foot);
        _drawer.Controls.Add(_drawerHeader);
    }

    /// <summary>Keeps the drawer on the window's right edge, between the header band and the footer.</summary>
    private void PlaceDrawer()
    {
        if (WindowState == FormWindowState.Minimized)
            return;
        var width = Math.Min(LogicalToDeviceUnits(DrawerWidth), ClientSize.Width);
        _drawer.Bounds = new Rectangle(ClientSize.Width - width, HeaderHeight, width, Math.Max(0, ClientSize.Height - HeaderHeight - _footer.Height));
    }

    /// <summary>Shows <paramref name="swing"/> in the drawer; <paramref name="refresh"/> keeps the selected item (the 30 s reload).</summary>
    private void OpenDrawer(SwingRow swing, bool refresh = false)
    {
        IReadOnlyList<SwingItem> items;
        try
        {
            items = swing.Stage == "closed" ? _loader.SwingItems(swing.Id) : [];
        }
        catch (SqliteException)
        {
            items = [];
        }

        var same = _drawerSwing?.Id == swing.Id;
        _drawerSwing = swing;
        var kept = same && _itemList.SelectedItems.Count > 0 ? _itemList.SelectedItems[0].SubItems[1].Text + "|" + _itemList.SelectedItems[0].SubItems[0].Text : null;
        var probes = items.Where(i => i.Source != Attribution.ProcessSource).ToList();
        var writers = items.Where(i => i.Source == Attribution.ProcessSource).Take(DetailWritersShown).ToList();
        var largest = Math.Max(1, probes.Select(i => Math.Abs(i.DeltaBytes)).Append(Math.Abs(swing.UnaccountedBytes ?? 0)).Max());
        var largestWriter = Math.Max(1, writers.Select(i => i.DeltaBytes).DefaultIfEmpty(1).Max());

        _itemList.BeginUpdate();
        _itemList.Items.Clear();
        foreach (var i in probes)
            AddDetailRow(i.Source == "usn" ? "File" : "Probe", i.Label, i.Path, new DetailRow("probe", i.DeltaBytes, i.Path, Math.Abs(i.DeltaBytes) / (double)largest), i.Path);
        if (swing.Stage == "closed")
        {
            var note = swing.ProbeWindowStart is null
                ? "No Probe was measured on both sides of this Swing (the Collector stopped before the after-side run), so all of it is Unaccounted."
                : UnaccountedHint;
            AddDetailRow("", "Unaccounted", note, new DetailRow("unaccounted", swing.UnaccountedBytes ?? 0, null, Math.Abs(swing.UnaccountedBytes ?? 0) / (double)largest), note);
        }
        // Top Writers last: the Probe rows and Unaccounted above them add up to the Swing's change in used space.
        foreach (var w in writers)
            AddDetailRow("Top Writer", w.Label, w.Path, new DetailRow("process", w.DeltaBytes, w.Path, w.DeltaBytes / (double)largestWriter),
                (w.Path ?? w.Label) + "\nBytes written during the Swing: a ranking signal, not bytes on disk (not counted in Attributed).");
        foreach (ListViewItem item in _itemList.Items)
            item.Selected = kept is not null && item.SubItems[1].Text + "|" + item.SubItems[0].Text == kept;
        // Nothing chosen yet: select the first row with a location, so "Open location" works straight away.
        if (_itemList.SelectedItems.Count == 0
            && _itemList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is DetailRow { Path.Length: > 0 }) is { } first)
            first.Selected = true;
        _itemList.EndUpdate();
        if (_itemList.Items.Count > 0)
            _itemList.EnsureVisible(_itemList.SelectedIndices.Count > 0 ? _itemList.SelectedIndices[0] : 0);
        _itemList.EmptyText = swing.Stage switch
        {
            "open" => "Free space is still moving. Attribution runs once the Swing settles and the Probes have been measured again.",
            "closing" => "Attributing… waiting for the Probe run after this Swing's end (it starts as soon as the Swing closes).",
            _ => "Nothing changed by 50 MB or more in a Probe.",
        };
        _itemList.UpdateEmpty();
        _openLocation.Enabled = SelectedItemPath() is not null;

        // Mark the Swing in the list and the lane without reopening it from there.
        _syncingSelection = true;
        foreach (ListViewItem item in _swingList.Items)
            item.Selected = item.Tag is SwingRow w && w.Id == swing.Id;
        _syncingSelection = false;
        foreach (var chip in _lane.Controls.OfType<SwingChip>())
            chip.Selected = chip.Swing.Id == swing.Id;

        if (!_drawer.Visible)
        {
            PlaceDrawer();
            _drawer.Visible = true;
            _drawer.BringToFront();
        }
        _drawerHeader.Invalidate();
        if (!refresh)
            _itemList.Focus();
    }

    private void CloseDrawer()
    {
        _drawer.Visible = false;
        _drawerSwing = null;
        foreach (var chip in _lane.Controls.OfType<SwingChip>())
            chip.Selected = false;
    }

    private void PaintDrawerHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        if (_drawerSwing is not { } swing)
            return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var kind = swing.Kind == "growth" ? Theme.Growth : Theme.Release;

        // Pills: the kind, the stage, and Downtime when it applies.
        var x = 20;
        void Pill(string text, Color fore, Color back)
        {
            var size = TextRenderer.MeasureText(text, Theme.BodyStrong, Size.Empty, TextFormatFlags.NoPadding);
            var pill = new Rectangle(x, 18, size.Width + 22, 26);
            using (var path = CardPanel.Rounded(pill, 12))
            using (var fill = new SolidBrush(back))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, text, Theme.BodyStrong, pill, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x = pill.Right + 8;
        }
        Pill($"{KindName(swing)} Swing", kind, Tint.Over(kind, Theme.Card, 0.15));
        var (stageFore, stageBack) = swing.Stage == "closed" ? (Theme.Muted, Theme.Grid) : (Theme.Amber, Theme.AmberGround);
        Pill(swing.Stage switch { "open" => "open", "closing" => "attributing…", _ => "closed" }, stageFore, stageBack);
        if (swing.AcrossDowntime)
            Pill("across Downtime", Theme.Muted, Theme.Grid);

        var y = 18 + 26 + 14;
        var width = _drawerHeader.ClientSize.Width - 40;
        var change = swing.UsedDeltaBytes is { } used ? SizeText.SignedGb(-used) : "Still moving";
        TextRenderer.DrawText(g, change, Theme.Hero, new Rectangle(18, y, width, Theme.Hero.Height), Theme.Text, flags);
        y += Theme.Hero.Height;
        var end = swing.TsEnd is { } te ? ShortTime(te) : "now";
        var line = $"free space · {ShortTime(swing.TsStart)} → {end}";
        if (swing.ProbeWindowStart is { } ws && swing.ProbeWindowEnd is { } we)
            line += $" · Probe Window {ShortTime(ws)}–{ShortTime(we)}";
        TextRenderer.DrawText(g, line, Theme.Small, new Rectangle(20, y, width, Theme.Small.Height + 2), Theme.Muted, flags);
        y += Theme.Small.Height + 14;

        // Attributed and Unaccounted as two small figures.
        var half = (width - 10) / 2;
        void Figure(int left, string caption, string value)
        {
            var box = new Rectangle(left, y, half, DrawerFigureHeight);
            using (var path = CardPanel.Rounded(box, 10))
            using (var fill = new SolidBrush(Theme.PlotGround))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, caption, Theme.Small, new Rectangle(box.X + 12, box.Y + 10, box.Width - 24, Theme.Small.Height), Theme.Muted, flags);
            TextRenderer.DrawText(g, value, Theme.Section, new Rectangle(box.X + 12, box.Y + 12 + Theme.Small.Height, box.Width - 24, Theme.Section.Height), Theme.Text, flags);
        }
        Figure(20, "Attributed", swing.AttributedBytes is { } a ? SizeText.SignedGb(a) : "—");
        Figure(30 + half, "Unaccounted", swing.UnaccountedBytes is { } u ? SizeText.SignedGb(u) : "—");
    }

    private void AddDetailRow(string source, string label, string? location, DetailRow row, string? tip)
    {
        var item = new ListViewItem(source) { Tag = row, ToolTipText = tip };
        item.SubItems.Add(label);
        item.SubItems.Add(location ?? "");
        _itemList.Items.Add(item);
    }

    /// <summary>
    /// A drawer row in three lines: source caption, label and change (coloured by direction); a bar scaled to the
    /// largest row; the location.
    /// </summary>
    private static bool PaintItemCell(Graphics g, Rectangle cell, ListViewItem item, int column)
    {
        if (item.Tag is not DetailRow row || column != 0)
            return false;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var (color, text) = row.Source switch
        {
            "process" => (Theme.Fill, SizeText.Compact(row.DeltaBytes) + " written"),
            "unaccounted" => (Theme.Unaccounted, SignedSize(row.DeltaBytes)),
            _ => (row.DeltaBytes >= 0 ? Theme.Growth : Theme.Release, SignedSize(row.DeltaBytes)),
        };
        var y = cell.Y + 8;
        var changeWidth = TextRenderer.MeasureText(text, Theme.Mono, Size.Empty, TextFormatFlags.NoPadding).Width;
        const int captionWidth = 84;
        TextRenderer.DrawText(g, item.Text.ToUpperInvariant(), Theme.Caption, new Rectangle(cell.X, y + 3, captionWidth - 6, Theme.Caption.Height), Theme.Faint, flags);
        TextRenderer.DrawText(g, item.SubItems[1].Text, row.Source == "unaccounted" ? Theme.BodyStrong : Theme.Body,
            new Rectangle(cell.X + captionWidth, y, Math.Max(0, cell.Width - captionWidth - changeWidth - 10), Theme.Body.Height), Theme.Text, flags);
        var fore = row.Source is "process" or "unaccounted" ? Theme.Muted : color;
        TextRenderer.DrawText(g, text, Theme.Mono, new Rectangle(cell.Right - changeWidth, y + 1, changeWidth, Theme.Body.Height), fore, flags);
        y += Theme.Body.Height + 6;
        var track = new Rectangle(cell.X, y, cell.Width, 4);
        using (var trackBrush = new SolidBrush(Theme.Grid))
            g.FillRectangle(trackBrush, track);
        using (var bar = new SolidBrush(color))
            g.FillRectangle(bar, track with { Width = Math.Max(2, (int)(track.Width * Math.Min(1, row.Share))) });
        y += 4 + 6;
        TextRenderer.DrawText(g, item.SubItems[2].Text, Theme.MonoSmall, new Rectangle(cell.X, y, cell.Width, Theme.MonoSmall.Height + 2), Theme.Faint,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
        return true;
    }

    /// <summary><c>+2.0 GB</c>, <c>−340 MB</c>: Swing items start at 50 MB, too small for one decimal of a GB.</summary>
    private static string SignedSize(long bytes) =>
        bytes == 0 ? "0 MB" : (bytes < 0 ? "−" : "+") + SizeText.Compact(Math.Abs(bytes));

    private string? SelectedItemPath() =>
        _itemList.SelectedItems.Count > 0 && _itemList.SelectedItems[0].Tag is DetailRow { Path: { Length: > 0 } path } ? path : null;

    /// <summary>Explorer at <paramref name="path"/>: a folder opened, a file (a program's exe) selected, or the nearest parent that still exists.</summary>
    private void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var target = OpenLocation.Resolve(path, File.Exists, Directory.Exists);
        if (target is null)
        {
            _footer.Text = $"{path} no longer exists, nor does any folder above it";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target.Arguments) { UseShellExecute = true })?.Dispose();
            if (!string.Equals(target.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                _footer.Text = $"{path} isn't there any more (or isn't readable from your account): opened {target.Path}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _footer.Text = $"could not open Explorer: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- Top Writers

    private void ShowWriters(DashboardSnapshot s)
    {
        _writersIntro.SetParts(
            ($"Bytes written over {s.Range.Description()}, all drives.   ", Theme.Text, Theme.Body),
            ("Ranking signal", Theme.Lime, Theme.BodyStrong),
            (", not bytes on disk. The counters also count network and other non-disk I/O.", Theme.Muted, Theme.Body));
        _writerList.BeginUpdate();
        _writerList.Items.Clear();
        var top = s.Writers.Count > 0 ? Math.Max(1, s.Writers[0].WriteBytes) : 1;
        for (var i = 0; i < s.Writers.Count; i++)
        {
            var w = s.Writers[i];
            var item = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture)) { Tag = (double)w.WriteBytes / top };
            item.SubItems.Add(w.OtherPaths > 0 ? $"{w.Name}  ({w.OtherPaths + 1} paths)" : w.Name);
            item.SubItems.Add(SizeText.Compact(w.WriteBytes));
            item.SubItems.Add(w.ExePath ?? "");
            item.ToolTipText = w.OtherPaths > 0
                ? $"{w.ExePath}\n(+{w.OtherPaths} other install path{(w.OtherPaths == 1 ? "" : "s")}, summed)"
                : w.ExePath;
            _writerList.Items.Add(item);
        }
        _writerList.EndUpdate();
        _writerList.UpdateEmpty();
    }

    /// <summary>The Written column: a pink bar scaled to the top writer, then the size.</summary>
    private static bool PaintWriterCell(Graphics g, Rectangle cell, ListViewItem item, int column)
    {
        if (column != 2 || item.Tag is not double share)
            return false;
        PaintBar(g, cell, share, Theme.Line, item.SubItems[2].Text, ramp: true);
        return true;
    }

    /// <summary>A bar on a track scaled to <paramref name="share"/>, with the size right-aligned after it.</summary>
    private static void PaintBar(Graphics g, Rectangle cell, double share, Color color, string text, int barMax = 260, bool ramp = false)
    {
        var sizeWidth = TextRenderer.MeasureText("000.0 GB", Theme.Mono).Width;
        var track = new Rectangle(cell.X, cell.Y + (cell.Height / 2) - 4, Math.Clamp(cell.Width - sizeWidth - 10, 0, barMax), 8);
        using (var trackBrush = new SolidBrush(Theme.Grid))
            g.FillRectangle(trackBrush, track);
        var filled = track with { Width = Math.Max(2, (int)(track.Width * share)) };
        using (var bar = ramp && track.Width > 0
            ? (Brush)new System.Drawing.Drawing2D.LinearGradientBrush(track with { Width = track.Width + 1 }, Theme.RampFrom, Theme.RampTo, 0f)
            : new SolidBrush(color))
            g.FillRectangle(bar, filled);
        var label = new Rectangle(track.Right + 10, cell.Y, sizeWidth, cell.Height);
        TextRenderer.DrawText(g, text, Theme.Mono, label, Theme.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    // ---------------------------------------------------------------- status

    private void ShowStatus(PipeResponse? ping, DashboardSnapshot s)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var running = CollectorStatus.IsRunning(ping, now);
        var last = ping?.LastSampleTs ?? s.LatestTs;
        _statusDot.ForeColor = running ? Theme.Lime : Theme.Red;
        _statusDot.Invalidate();
        _status.Text = running
            ? $"Collector running · {Ago(now - last!.Value)}"
            : last is { } ts ? $"Collector not running · last sample {LocalTime(ts, DashboardRange.Days7)}" : "Collector not running · no samples yet";
        if (_probeNow.Text == "Probe now")
            _probeNow.Enabled = running;
        _tips.SetToolTip(_probeNow, running ? _probeInfo.Text : "Probe now needs the Collector running");
    }

    private void ShowError(SqliteException ex)
    {
        _footer.Text = File.Exists(_loader.DatabasePath)
            ? $"cannot read the database ({ex.SqliteErrorCode})"
            : "no database yet. Is the Collector installed?";
        _statusDot.ForeColor = Theme.Red;
        _statusDot.Invalidate();
        _status.Text = "Collector not running";
        if (_last is null)
        {
            // Nothing was ever read: say so where "Loading…" stood.
            foreach (var list in new[] { _probeList, _swingList, _writerList })
            {
                list.EmptyText = "Nothing to show: " + _footer.Text;
                list.UpdateEmpty();
            }
        }
    }

    private static string Ago(long ms) => ms switch
    {
        < 60_000 => $"{Math.Max(0, ms / 1000)} s ago",
        < 3_600_000 => $"{ms / 60_000} min ago",
        < 172_800_000 => $"{ms / 3_600_000} h ago",
        _ => $"{ms / 86_400_000} days ago",
    };

    private static string LocalTime(long ts, DashboardRange range)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
        return range is DashboardRange.Hours6 or DashboardRange.Hours24 && local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary><c>05:35</c> today, otherwise <c>Tue 05:35</c>.</summary>
    private static string ShortTime(long ts)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
        return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.CurrentCulture);
    }

    private static double OaDate(long ts) => DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToOADate();

    private static SpColor Sp(System.Drawing.Color c) => SpColor.FromARGB((uint)c.ToArgb());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tips.Dispose();
            _probeList.SmallImageList?.Dispose();
            _swingList.SmallImageList?.Dispose();
            _itemList.SmallImageList?.Dispose();
            _writerList.SmallImageList?.Dispose();
        }
        base.Dispose(disposing);
    }
}
