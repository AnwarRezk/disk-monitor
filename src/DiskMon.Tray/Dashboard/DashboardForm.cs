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
/// The dashboard window (design G, Violet with additions): Volume chips and "Probe now" in the header; free space,
/// change, lowest and a "Full in" forecast; a chart with warning lines, Swing spans, a hover read-out and drag-to-zoom;
/// and tabs for where the space is (Probes with reclaim hints), Swings (with what changed) and Top Writers.
/// Reads the database only (PLAN §5.6), so it works with the Collector stopped. Lays itself out from the
/// minimum size up to maximised, and reopens with the last Volume, range, tab and window placement.
/// </summary>
internal sealed class DashboardForm : Form
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private const string SpaceTab = "space", SwingsTab = "swings", WritersTab = "writers";
    private const int HeaderHeight = 60;
    private const int NarrowWidth = 1100;
    private static readonly int ProbeRowHeight = Theme.Body.Height + Theme.MonoSmall.Height + 14;

    private readonly DashboardLoader _loader;
    private readonly PipeClient _pipe;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _persist;
    private readonly ToolTip _tips = new() { InitialDelay = 300 };

    // Header
    private readonly FlowLayoutPanel _volumeChips = new() { AutoSize = true, WrapContents = false, Margin = new Padding(16, 14, 0, 0), BackColor = Theme.Ground };
    private readonly Label _probesCaption = Caption("PROBES");
    private readonly Label _probeInfo = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Body, Margin = new Padding(8, 21, 10, 0) };
    private readonly ToggleButton _probeNow = new("Probe now", ToggleLook.Action) { Margin = new Padding(0, 15, 24, 0), AccessibleName = "Measure every Probe now" };
    private readonly Label _collectorCaption = Caption("COLLECTOR");
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Text, Font = Theme.Body, Margin = new Padding(8, 21, 0, 0) };
    private readonly Panel _statusDot = new() { Size = new Size(8, 8), Margin = new Padding(0, 28, 0, 0), BackColor = Theme.Ground };

    // Figures
    private readonly WrapRow _figures = new() { Dock = DockStyle.Top, Gap = 24 };
    private readonly TightLabel _freeCaption = CaptionTight("FREE");
    private readonly TightLabel _free = new() { Font = Theme.Hero, ForeColor = Theme.Text, Text = "—" };
    private readonly TightLabel _ofTotal = FigureTight();
    private readonly TightLabel _changeCaption = CaptionTight("CHANGE");
    private readonly TightLabel _change = FigureTight();
    private readonly TightLabel _lowest = FigureTight();
    private readonly NoteCard _forecast = new();
    private readonly List<ToggleButton> _rangeButtons = [];

    // Chart
    private readonly FormsPlot _plot = new() { Dock = DockStyle.Fill, BackColor = Theme.Card };
    private readonly TipBox _tip = new();
    private readonly RichLine _legend = new() { Dock = DockStyle.Bottom, Height = 24 };
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
    private readonly DarkListView _probeList = new(rowHeight: ProbeRowHeight) { Dock = DockStyle.Fill };
    private readonly DarkListView _swingList = new() { Dock = DockStyle.Fill };
    private readonly Panel _swingDetail = new() { Dock = DockStyle.Bottom, Height = 150, BackColor = Theme.PlotGround, Visible = false, Padding = new Padding(20, 10, 20, 10) };
    private readonly RichLine _writersIntro = new() { Dock = DockStyle.Fill };
    private readonly DarkListView _writerList = new() { Dock = DockStyle.Fill };
    private readonly Label _footer = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Faint, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Ground };

    private TableLayoutPanel? _root;
    private readonly List<Panel> _introBars = [];
    private bool _wasNarrow;
    private string _layoutNote = "";
    private int _probeBarMax = 170;
    private Control? _ofTotalStat;
    private Control? _changeStat;
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
    private int _hoverIndex = -1;
    private double? _dragFrom;
    private int _dragStartPx;
    private long? _copiedProbe;
    private IReadOnlyList<SwingItem> _detailItems = [];
    private SwingRow? _detailSwing;

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
        Padding = new Padding(24, 8, 24, 4);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        BuildLayout();
        StylePlot();
        WireChartMouse();

        _timer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await ReloadAsync();
        VisibleChanged += (_, _) => _timer.Enabled = Visible;
        Resize += (_, _) => ApplyProportions();
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
            await ReloadAsync();
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

    /// <summary>Where the main parts sit, in client pixels, so a screenshot run can say exactly what got which size.</summary>
    private string DescribeLayout()
    {
        string Box(Control? c) => c is null ? "-" : $"{RectangleToClient(c.RectangleToScreen(c.ClientRectangle))} visible={c.Visible}";
        var lines = new List<string>
        {
            $"dpi {DeviceDpi} window {Size} client {ClientSize} state {WindowState}",
            $"note {_layoutNote}",
            $"root {Box(_root)} rows [{string.Join(", ", _root?.GetRowHeights() ?? [])}] styles [{string.Join(", ", _root?.RowStyles.Cast<RowStyle>().Select(r => $"{r.SizeType} {r.Height}") ?? [])}]",
            $"chartCard {Box(_chartCard)}",
            $"figures {Box(_figures)}",
            $"legend {Box(_legend)}",
            $"plot {Box(_plot)}",
            $"probeList {Box(_probeList)}",
            $"swingList {Box(_swingList)}",
            $"writerList {Box(_writerList)}",
            $"footer {Box(_footer)}",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Ground, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 460));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = BuildHeader();
        root.Controls.Add(header, 0, 0);
        // The divider sits in the gap under the header, where no child control can cover it.
        root.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Hairline);
            var y = header.Bottom + 5;
            e.Graphics.DrawLine(pen, 0, y, root.Width, y);
        };
        root.Controls.Add(BuildChartCard(), 0, 1);
        root.Controls.Add(BuildTabsCard(), 0, 2);
        Controls.Add(root);
        Controls.Add(_footer);
        root.SizeChanged += (_, _) => ApplyProportions();
    }

    /// <summary>
    /// Splits the height between the chart and the tabs (about 56/44, never squeezing either below a usable size)
    /// and drops the header's captions in a narrow window. Skipped while minimised.
    /// </summary>
    private void ApplyProportions()
    {
        if (_root is null || _chartCard is null || WindowState == FormWindowState.Minimized || ClientSize.Height <= 0)
            return;
        var narrow = ClientSize.Width < NarrowWidth;
        _probesCaption.Visible = _collectorCaption.Visible = _probeInfo.Visible = !narrow;
        foreach (var intro in _introBars)
            intro.Visible = !narrow;
        if (narrow != _wasNarrow && _last is not null)
        {
            _wasNarrow = narrow;
            ShowFigures(_last);
            ShowProbes(_last);
        }

        var available = _root.ClientSize.Height - HeaderHeight;
        var barMax = narrow ? 110 : 170;
        if (_probeList.Columns.Count == 4)
        {
            _probeList.Columns[1].Tag = -(barMax + 10 + TextRenderer.MeasureText("000.0 GB", Theme.Mono).Width + 24);
            _probeList.FillLastColumn();
        }
        _probeBarMax = barMax;
        _figures.Gap = narrow ? 18 : 24;
        _ofTotalStat!.Visible = !narrow;
        _changeStat!.MinimumSize = narrow ? Size.Empty : new Size(TextRenderer.MeasureText("CHANGE SINCE WED 00:00", Theme.Caption).Width, 0);
        _figures.PerformLayout();
        // Room for the tab strip, column headers and at least two whole Probe rows.
        var tabsMin = Math.Max(narrow ? 0 : 230, 45 + 32 + (int)(2.2 * ProbeRowHeight) + 8);
        const int plotMin = 150;
        var chartMin = _chartCard.Padding.Vertical + _figures.Height + 10 + _legend.Height + plotMin;
        var chart = (int)(available * (narrow ? 0.6 : 0.56));
        chart = Math.Max(chartMin, Math.Min(chart, available - tabsMin));
        var height = Math.Max(chartMin, chart);
        _layoutNote = $"layout {available}: chart {height}, figures {_figures.Height}";
        if (Math.Abs(_root.RowStyles[1].Height - height) > 0.5f)
        {
            _root.RowStyles[1].Height = height;
            // This often runs from inside a layout pass (a resize), where the table ignores the new row height until
            // something lays it out again: do that once the current pass has finished.
            if (_root.IsHandleCreated)
                _root.BeginInvoke(() => _root.PerformLayout());
        }
        UpdateTickDensity();
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, BackColor = Theme.Ground, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(0, 0, 0, 2) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = new Label { Text = "DiskMon", AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        _probesCaption.Margin = new Padding(0, 24, 0, 0);
        _collectorCaption.Margin = new Padding(0, 24, 10, 0);
        _probeNow.Click += async (_, _) => await ProbeNowAsync();
        _statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.Clear(Theme.Ground);
            using var brush = new SolidBrush(_statusDot.ForeColor);
            e.Graphics.FillEllipse(brush, 0, 0, 7, 7);
        };
        var dotAndStatus = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Ground, Margin = Padding.Empty };
        dotAndStatus.Controls.Add(_statusDot);
        dotAndStatus.Controls.Add(_status);

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_volumeChips, 1, 0);
        header.Controls.Add(_probesCaption, 3, 0);
        header.Controls.Add(_probeInfo, 4, 0);
        header.Controls.Add(_probeNow, 5, 0);
        header.Controls.Add(_collectorCaption, 6, 0);
        header.Controls.Add(dotAndStatus, 7, 0);
        return header;
    }

    private Control BuildChartCard()
    {
        var card = _chartCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 8), Margin = new Padding(0, 0, 0, 12) };

        _figures.Controls.Add(Stat(_freeCaption, _free));
        _figures.Controls.Add(_ofTotalStat = Stat(CaptionTight("OF TOTAL"), _ofTotal));
        // Wide enough for "CHANGE SINCE Tue 05:35", so the cards after it don't shift as the caption changes.
        var change = _changeStat = Stat(_changeCaption, _change);
        _figures.Controls.Add(change);
        _figures.Controls.Add(Stat(CaptionTight("LOWEST"), _lowest));
        _figures.Controls.Add(_forecast);
        var ranges = BuildRangeButtons();
        _figures.Controls.Add(ranges);
        _figures.Trailing = ranges;
        _figures.SizeChanged += (_, _) => ApplyProportions();
        _forecast.Set("FULL IN", "—", "needs 6 h of history", Theme.Muted, Theme.Card, Theme.Hairline);

        var gap = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Card };
        _plot.Controls.Add(_tip);
        card.Controls.Add(_plot);
        card.Controls.Add(_legend);
        card.Controls.Add(gap);
        card.Controls.Add(_figures);
        return card;
    }

    private Control BuildRangeButtons()
    {
        var group = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, BackColor = Theme.Card, Padding = new Padding(1),
            Margin = Padding.Empty, AccessibleName = "Range", AccessibleRole = AccessibleRole.Grouping,
        };
        group.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Hairline);
            e.Graphics.DrawRectangle(pen, 0, 0, group.Width - 1, group.Height - 1);
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

        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(20, 0, 0, 0), BackColor = Theme.Card, WrapContents = false };
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
        _probeList.EmptyText = "No Probe runs yet. Use Probe now, or wait for the Collector's next run (every 10 minutes).";
        _probeList.MouseClick += (_, e) => CopyHint(e.Location);
        _probeList.MouseMove += (_, e) =>
        {
            var hit = _probeList.HitTest(e.Location);
            _probeList.Cursor = hit.Item?.Tag is ProbeTag { Hint.Command: not null } && hit.Item.SubItems.IndexOf(hit.SubItem) == 3 ? Cursors.Hand : Cursors.Default;
        };
        _spacePage.Controls.Add(_probeList);
        _spacePage.Controls.Add(IntroBar(_spaceIntro));
    }

    private void BuildSwingsPage()
    {
        AddColumns(_swingList, ("Time", 20, HorizontalAlignment.Left), ("Volume", 10, HorizontalAlignment.Left),
            ("Kind", 14, HorizontalAlignment.Left), ("Change", 14, HorizontalAlignment.Right),
            ("Attributed", 14, HorizontalAlignment.Right), ("Unaccounted", 15, HorizontalAlignment.Right),
            ("Stage", 13, HorizontalAlignment.Left));
        _swingList.MonoColumns.UnionWith([0, 3, 4, 5]);
        _swingList.CellPainter = PaintStageCell;
        _swingList.EmptyText = "No Swings in this range. A Swing is recorded when free space moves by 1 GB or more within 15 minutes.";
        _swingList.SelectedIndexChanged += (_, _) => ShowSwingDetail();
        _swingDetail.Paint += PaintSwingDetail;
        _swingDetail.Resize += (_, _) => _swingDetail.Invalidate();
        _swingsPage.Controls.Add(_swingList);
        _swingsPage.Controls.Add(_swingDetail);
    }

    private void BuildWritersPage()
    {
        AddColumns(_writerList, ("#", 6, HorizontalAlignment.Left), ("Program", 26, HorizontalAlignment.Left),
            ("Written", 30, HorizontalAlignment.Left), ("Path", 38, HorizontalAlignment.Left));
        _writerList.MonoColumns.UnionWith([0, 3]);
        _writerList.PathColumns.Add(3);
        _writerList.CellPainter = PaintWriterCell;
        _writerList.ShowItemToolTips = true;
        _writerList.EmptyText = "No Top Writers recorded in this range.";
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
    }

    private static void AddColumns(DarkListView list, params (string Text, int Share, HorizontalAlignment Align)[] columns)
    {
        foreach (var (text, share, align) in columns)
            list.Columns.Add(new ColumnHeader { Text = text, Width = 100, TextAlign = align, Tag = share });
        list.ProportionalColumns = true;
    }

    private static Control Stat(TightLabel caption, TightLabel value)
    {
        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
        caption.Margin = new Padding(0, 0, 0, 6);
        stack.Controls.Add(caption);
        stack.Controls.Add(value);
        return stack;
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Muted, Margin = Padding.Empty };

    private static TightLabel CaptionTight(string text) => new() { Text = text, Font = Theme.Caption, ForeColor = Theme.Muted };

    private static TightLabel FigureTight() => new() { Font = Theme.Figure, ForeColor = Theme.Text, Text = "—" };

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
            BeginInvoke(() => _footer.Text += $" · chart drawn in {rendered.TotalMilliseconds:0} ms");
        };
        _plot.Resize += (_, _) => UpdateTickDensity();
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

    private Coordinates Coordinates(Point location) =>
        _plot.Plot.GetCoordinates(new Pixel(location.X / _plot.DisplayScale, location.Y / _plot.DisplayScale));

    private void ShowHover(Point location)
    {
        if (_points.Count == 0 || _hoverLine is null)
            return;
        var x = Coordinates(location).X;
        var i = Nearest(x);
        var px = _plot.Plot.GetPixel(new Coordinates(OaDate(_points[i].Ts), 0)).X * _plot.DisplayScale;
        if (Math.Abs(px - location.X) > 40)
        {
            HideHover();
            _plot.Refresh();
            return;
        }
        if (i == _hoverIndex)
            return;
        _hoverIndex = i;
        var p = _points[i];
        _hoverLine.X = OaDate(p.Ts);
        _hoverLine.IsVisible = true;
        _tip.Set(DateTimeOffset.FromUnixTimeMilliseconds(p.Ts).LocalDateTime.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), $"{SizeText.Gb(p.FreeBytes)} free");
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
        _freeCaption.Text = ClientSize.Width < NarrowWidth && s.Summary?.FreePercent is { } narrowPct
            ? $"FREE ON {s.Volume} · {narrowPct.ToString("0.0", CultureInfo.InvariantCulture)}% OF {SizeText.Gb(s.Summary.TotalBytes)}"
            : $"FREE ON {s.Volume}";
        _changeCaption.Text = s.Summary is { } first && first.FirstTs - s.FromTs > 2 * (long)TimeSpan.FromMinutes(10).TotalMilliseconds
            ? $"CHANGE SINCE {ShortTime(first.FirstTs).ToUpperInvariant()}"
            : $"CHANGE {s.Range.Label().ToUpperInvariant()}";
        ShowForecast(s);
        if (s.Summary is not { } sum)
        {
            _free.Text = "—";
            _free.ForeColor = Theme.Text;
            _ofTotal.Text = _change.Text = _lowest.Text = "—";
            _change.ForeColor = Theme.Text;
            _figures.PerformLayout();
            return;
        }

        _free.Text = SizeText.Gb(sum.FreeBytes);
        _free.ForeColor = FreeSpaceStatus.Level(sum.FreeBytes, sum.TotalBytes, _loader.TrayOptions) switch
        {
            FreeSpaceLevel.Red => Theme.Red,
            FreeSpaceLevel.Amber => Theme.Amber,
            _ => Theme.Text,
        };
        _ofTotal.Text = sum.FreePercent is { } pct
            ? $"{pct.ToString("0.0", CultureInfo.InvariantCulture)}% · {SizeText.Gb(sum.TotalBytes)}"
            : "—";
        _change.Text = SizeText.SignedGb(sum.ChangeBytes);
        _change.ForeColor = Math.Abs(sum.ChangeBytes) < Bytes.GB / 20 ? Theme.Text : sum.ChangeBytes > 0 ? Theme.Lime : Theme.Line;
        _lowest.Text = SizeText.Gb(sum.LowestFreeBytes);
        _figures.PerformLayout();
    }

    private void ShowForecast(DashboardSnapshot s)
    {
        if (s.Forecast is not { } f)
        {
            _forecast.Set("FULL IN", "—", "needs 6 h of history", Theme.Muted, Theme.Card, Theme.Hairline);
            return;
        }
        var basis = f.SpanDays >= 6.5 ? "the 7-day trend" : $"the trend since {ShortTime(s.ForecastFromTs)}";
        var perDay = $"{SizeText.SignedGb((long)f.BytesPerDay)}/day";
        if (f.DaysToFull is not { } days)
        {
            _forecast.Set("FULL IN", "Not filling", ClientSize.Width < NarrowWidth ? "steady or rising" : $"steady or rising at {basis}", Theme.Lime, Theme.LimeGround, Theme.LimeEdge);
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
            _ => (Theme.Text, Theme.PlotGround, Theme.Hairline),
        };
        _forecast.Set("FULL IN", value, ClientSize.Width < NarrowWidth ? perDay : $"at {basis}, {perDay}", accent, ground, edge);
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
            var top = high + pad;
            if (warnings.Count > 0)
            {
                // Room above the highest warning line for its label (about 20 px of the plot's height).
                var plotPx = Math.Max(60, _plot.Height - 40);
                var perPx = (top - floor) / plotPx;
                top = Math.Max(top, highestWarning + (22 * perPx));
            }
            if (_yTicks is not null)
                _yTicks.IntegerTicksOnly = top - floor >= 3;
            plot.Axes.SetLimitsY(floor, top);
        }
        else
        {
            var empty = plot.Add.Annotation($"No samples for {s.Volume} in {s.Range.Description()}");
            empty.LabelFontColor = Sp(Theme.Muted);
            empty.LabelBackgroundColor = Sp(Theme.PlotGround);
            empty.LabelBorderColor = Sp(Theme.PlotGround);
            empty.Alignment = Alignment.MiddleCenter;
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
        ShowLegend(s, warnings);
        _footer.Text = $"updated {DateTime.Now:HH:mm:ss} · read in {s.ReadTime.TotalMilliseconds:0} ms · {_points.Count} points"
            + (s.Mode == ReadOnlyOpenMode.Immutable ? " · read-only snapshot (Collector stopped)" : "")
            + (_persist ? "" : $" · {_layoutNote}");
        _plot.Refresh();
    }

    private void ShowLegend(DashboardSnapshot s, List<string> warnings)
    {
        var parts = new List<(string, Color, Font)>();
        if (_zoom is not null)
            parts.Add(("Zoomed in: double-click or Esc to reset      ", Theme.Lime, Theme.BodyStrong));
        else
            parts.Add((ClientSize.Width < NarrowWidth ? "Drag to zoom      " : "Hover for exact values   ·   drag across to zoom      ", Theme.Muted, Theme.Small));
        parts.Add(("■ ", Theme.Growth, Theme.Small));
        parts.Add(("Growth Swing   ", Theme.Muted, Theme.Small));
        parts.Add(("■ ", Theme.Release, Theme.Small));
        parts.Add(("Release Swing", Theme.Muted, Theme.Small));
        if (warnings.Count > 0)
        {
            parts.Add(("      - - ", _warningColor, Theme.Small));
            parts.Add(("tray warning levels", Theme.Muted, Theme.Small));
        }
        _legend.SetParts([.. parts]);
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
                (ClientSize.Width < NarrowWidth ? "" : $"   ·   change over {s.Range.Description()}   ·   click a command to copy it" + (empty > 0 ? $"   ·   {empty} empty Probes hidden" : ""), Theme.Muted, Theme.Small));
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
        _probeInfo.Text = s.LatestProbeTs is { } ts ? $"last run {Ago(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts)}" : "no run yet";
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
                TextRenderer.DrawText(g, tag.Probe.Path, Theme.MonoSmall, new Rectangle(cell.X, top + nameHeight + 2, cell.Width, Theme.MonoSmall.Height), Theme.Faint,
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
                    var text = _copiedProbe == tag.Probe.Id ? "Copied ✓" : command;
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
        try
        {
            Clipboard.SetText(command);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }
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

    // ---------------------------------------------------------------- Swings

    private void ShowSwings(DashboardSnapshot s)
    {
        var selected = (_swingList.SelectedItems.Count > 0 ? _swingList.SelectedItems[0].Tag as SwingRow : null)?.Id;
        _swingList.BeginUpdate();
        _swingList.Items.Clear();
        foreach (var w in s.Swings)
        {
            var item = new ListViewItem(LocalTime(w.TsStart, s.Range)) { Tag = w };
            item.SubItems.Add(w.Volume);
            item.SubItems.Add(new ListViewItem.ListViewSubItem(item, (w.Kind == "growth" ? "Growth" : "Release") + (w.AcrossDowntime ? " ↯" : ""))
            {
                ForeColor = w.Kind == "growth" ? Theme.Growth : Theme.Release,
            });
            // used_delta is +growth / −release in used space; show the change in free space, matching the chart.
            item.SubItems.Add(w.UsedDeltaBytes is { } used ? SizeText.SignedGb(-used) : "…");
            item.SubItems.Add(w.AttributedBytes is { } a ? SizeText.Gb(a) : "—");
            item.SubItems.Add(w.UnaccountedBytes is { } u ? SizeText.Gb(u) : "—");
            item.SubItems.Add(w.Stage switch { "open" => "open", "closing" => "attributing…", _ => "closed" });
            item.Selected = w.Id == selected;
            _swingList.Items.Add(item);
        }
        _swingList.EndUpdate();
        _swingList.UpdateEmpty();
        _swingsTab.Text = s.Swings.Count > 0 ? $"Swings · {s.Swings.Count}" : "Swings";
        if (_swingList.SelectedItems.Count == 0)
            ShowSwingDetail();
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

    private void ShowSwingDetail()
    {
        _detailSwing = _swingList.SelectedItems.Count > 0 ? _swingList.SelectedItems[0].Tag as SwingRow : null;
        if (_detailSwing is null)
        {
            _swingDetail.Visible = false;
            return;
        }
        try
        {
            _detailItems = _loader.SwingItems(_detailSwing.Id);
        }
        catch (SqliteException)
        {
            _detailItems = [];
        }
        _swingDetail.Height = Math.Clamp(60 + (Math.Min(5, _detailItems.Count(i => i.Source != "process")) * 22) + 24, 110, 220);
        _swingDetail.Visible = true;
        _swingDetail.Invalidate();
    }

    private void PaintSwingDetail(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.PlotGround);
        if (_detailSwing is not { } swing)
            return;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        var x = _swingDetail.Padding.Left;
        var y = _swingDetail.Padding.Top;
        var width = _swingDetail.ClientSize.Width - _swingDetail.Padding.Horizontal;
        var end = swing.TsEnd is { } te ? LocalTime(te, DashboardRange.Hours24) : "now";
        TextRenderer.DrawText(g, $"WHAT CHANGED · {LocalTime(swing.TsStart, DashboardRange.Hours24)} → {end}" + (swing.AcrossDowntime ? " · across Downtime" : ""),
            Theme.Caption, new Rectangle(x, y, width, 14), Theme.Muted, flags);
        y += 24;

        var probes = _detailItems.Where(i => i.Source != "process").Take(5).ToList();
        var rows = probes.Select(i => (i.Label, i.DeltaBytes, Theme.Growth)).ToList();
        if (swing.UnaccountedBytes is { } u && u != 0)
            rows.Add(("Unaccounted", u, Theme.Unaccounted));
        if (rows.Count == 0)
        {
            var why = swing.Stage == "closed" ? "No Probe changed by 50 MB or more during this Swing." : "Attribution runs once the Swing closes and the Probes have been measured again.";
            TextRenderer.DrawText(g, why, Theme.Body, new Rectangle(x, y, width, 18), Theme.Muted, flags);
            y += 26;
        }
        else
        {
            var labelWidth = Math.Min(260, width / 3);
            var barWidth = Math.Min(320, width / 3);
            var max = Math.Max(1, rows.Max(r => Math.Abs(r.Item2)));
            foreach (var (label, delta, color) in rows)
            {
                TextRenderer.DrawText(g, label, Theme.Body, new Rectangle(x, y, labelWidth, 18), Theme.Text, flags);
                var bar = new Rectangle(x + labelWidth + 12, y + 6, Math.Max(2, (int)(barWidth * Math.Abs(delta) / (double)max)), 6);
                using (var b = new SolidBrush(color == Theme.Unaccounted ? Theme.Unaccounted : delta >= 0 ? Theme.Growth : Theme.Release))
                    g.FillRectangle(b, bar);
                TextRenderer.DrawText(g, (delta >= 0 ? "+" : "−") + SizeText.Gb(Math.Abs(delta)), Theme.Mono,
                    new Rectangle(x + labelWidth + barWidth + 24, y, 120, 18), color == Theme.Unaccounted ? Theme.Muted : delta >= 0 ? Theme.Growth : Theme.Release, flags);
                y += 22;
            }
        }
        var writers = _detailItems.Where(i => i.Source == "process").Take(4).Select(i => i.Label).ToList();
        if (writers.Count > 0)
        {
            TextRenderer.DrawText(g, $"Busiest writers during this Swing: {string.Join(", ", writers)} (ranking signal)", Theme.Small,
                new Rectangle(x, y + 4, width, 16), Theme.Muted, flags);
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
        PaintBar(g, cell, share, Theme.Line, item.SubItems[2].Text);
        return true;
    }

    /// <summary>A bar on a track scaled to <paramref name="share"/>, with the size right-aligned after it.</summary>
    private static void PaintBar(Graphics g, Rectangle cell, double share, Color color, string text, int barMax = 260)
    {
        var sizeWidth = TextRenderer.MeasureText("000.0 GB", Theme.Mono).Width;
        var track = new Rectangle(cell.X, cell.Y + (cell.Height / 2) - 4, Math.Clamp(cell.Width - sizeWidth - 10, 0, barMax), 8);
        using (var trackBrush = new SolidBrush(Theme.Grid))
            g.FillRectangle(trackBrush, track);
        using (var bar = new SolidBrush(color))
            g.FillRectangle(bar, track with { Width = Math.Max(2, (int)(track.Width * share)) });
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
            ? $"Running · {Ago(now - last!.Value)}"
            : last is { } ts ? $"Not running · last sample {LocalTime(ts, DashboardRange.Days7)}" : "Not running · no samples yet";
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
        _status.Text = "Not running";
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
            _writerList.SmallImageList?.Dispose();
        }
        base.Dispose(disposing);
    }
}
