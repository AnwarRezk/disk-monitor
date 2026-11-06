using System.ComponentModel;
using System.Drawing.Drawing2D;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Data;

namespace DiskDrift.Tray.Dashboard;

/// <summary>Colour helpers for the tinted pills and tiles.</summary>
internal static class Tint
{
    /// <summary><paramref name="color"/> laid over <paramref name="ground"/> at <paramref name="alpha"/> (0..1), as an opaque colour.</summary>
    public static Color Over(Color color, Color ground, double alpha) => Color.FromArgb(
        (int)Math.Round(ground.R + ((color.R - ground.R) * alpha)),
        (int)Math.Round(ground.G + ((color.G - ground.G) * alpha)),
        (int)Math.Round(ground.B + ((color.B - ground.B) * alpha)));

    /// <summary>Fills <paramref name="rect"/>'s corners with the parent's ground, then a rounded card with a hairline.</summary>
    public static void Card(Graphics g, Control control, Rectangle rect, int radius, Color fill, Color edge)
    {
        g.Clear(control.Parent?.BackColor ?? Theme.Ground);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CardPanel.Rounded(rect, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        using var pen = new Pen(edge);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// The summary's lead card: a ring gauge drawn like the logo (a 270° track, the used share in the violet→pink ramp)
/// beside free space on the Volume, its caption and a one-line detail.
/// </summary>
internal sealed class HeroCard : Control
{
    private string _caption = "FREE";
    private string _value = "—";
    private string _detail = "";
    private Color _valueColor = Theme.Text;
    private double? _used;

    public HeroCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty;
    }

    /// <param name="usedFraction">Used space as 0..1, or null when the Volume's size is unknown.</param>
    public void Set(string caption, string value, Color valueColor, string detail, double? usedFraction)
    {
        (_caption, _value, _valueColor, _detail, _used) = (caption, value, valueColor, detail, usedFraction);
        AccessibleName = $"{caption} {value}, {detail}" + (usedFraction is { } u ? $", {u:P0} used" : "");
        Invalidate();
    }

    private int GaugeSize => Math.Clamp(Height - 28, 56, 104);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Tint.Card(g, this, new Rectangle(0, 0, Width - 1, Height - 1), 12, Theme.Card, Theme.Hairline);

        var size = GaugeSize;
        var stroke = Math.Max(6f, size * 0.105f);
        var box = new RectangleF(18 + (stroke / 2), ((Height - size) / 2f) + (stroke / 2), size - stroke, size - stroke);
        using (var track = new Pen(Theme.Grid, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, box, 135, 270);
        if (_used is { } used && used > 0)
        {
            // The ramp runs from the gauge's bottom-left to its top-right, as in the logo.
            using var ramp = new LinearGradientBrush(new PointF(box.Left, box.Bottom), new PointF(box.Right, box.Top), Theme.RampFrom, Theme.RampTo);
            using var arc = new Pen(ramp, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, box, 135, (float)(270 * Math.Min(1, used)));
        }
        const TextFormatFlags centre = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var pct = _used is { } u2 ? $"{Math.Round(u2 * 100):0}%" : "—";
        var gauge = Rectangle.Round(box);
        var block = Theme.Section.Height + Theme.Small.Height;
        var top = gauge.Top + ((gauge.Height - block) / 2);
        TextRenderer.DrawText(g, pct, Theme.Section, new Rectangle(gauge.Left, top, gauge.Width, Theme.Section.Height), Theme.Text, centre);
        TextRenderer.DrawText(g, "used", Theme.Small, new Rectangle(gauge.Left, top + Theme.Section.Height, gauge.Width, Theme.Small.Height), Theme.Muted, centre);

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        var x = 18 + size + 20;
        var width = Math.Max(0, Width - x - 16);
        var height = Theme.Caption.Height + 4 + Theme.Hero.Height + Theme.Small.Height;
        var y = (Height - height) / 2;
        TextRenderer.DrawText(g, _caption, Theme.Caption, new Rectangle(x, y, width, Theme.Caption.Height), Theme.Muted, flags);
        y += Theme.Caption.Height + 2;
        TextRenderer.DrawText(g, _value, Theme.Hero, new Rectangle(x - 2, y, width + 2, Theme.Hero.Height), _valueColor, flags);
        y += Theme.Hero.Height;
        TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(x, y, width, Theme.Small.Height + 2), Theme.Muted, flags);
    }
}

/// <summary>A small summary card: a caption, a figure and a detail line, optionally tinted (the "Full in" forecast).</summary>
internal sealed class StatTile : Control
{
    private string _caption = "";
    private string _value = "—";
    private string _detail = "";
    private Color _valueColor = Theme.Text;
    private Color _ground = Theme.Card;
    private Color _edge = Theme.Hairline;
    private bool _hover;

    public StatTile(string caption)
    {
        _caption = caption;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Interactive { get; set; }

    public void Set(string caption, string value, string detail, Color? valueColor = null, Color? ground = null, Color? edge = null)
    {
        (_caption, _value, _detail) = (caption, value, detail);
        _valueColor = valueColor ?? Theme.Text;
        _ground = ground ?? Theme.Card;
        _edge = edge ?? Theme.Hairline;
        AccessibleName = $"{caption} {value}, {detail}";
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Interactive)
        {
            _hover = true;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover)
        {
            _hover = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var fill = Interactive && _hover ? Theme.Hover : _ground;
        var edge = Interactive && _hover ? Theme.SelectedEdge : _edge;
        Tint.Card(g, this, new Rectangle(0, 0, Width - 1, Height - 1), 12, fill, edge);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        var width = Math.Max(0, Width - 32);
        var height = Theme.Small.Height + 4 + Theme.TileValue.Height + 2 + Theme.Small.Height;
        var y = Math.Max(8, (Height - height) / 2);
        TextRenderer.DrawText(g, _caption, Theme.Small, new Rectangle(16, y, width, Theme.Small.Height), Theme.Muted, flags);
        y += Theme.Small.Height + 4;
        // A narrow tile steps the figure down a size before it would have to cut it short.
        bool Fits(Font f) => TextRenderer.MeasureText(_value, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= width + 1;
        var font = Fits(Theme.TileValue) ? Theme.TileValue : Fits(Theme.ForecastValue) ? Theme.ForecastValue : Theme.Section;
        TextRenderer.DrawText(g, _value, font, new Rectangle(15, y + Theme.TileValue.Height - font.Height, width + 1, font.Height), _valueColor, flags);
        y += Theme.TileValue.Height + 2;
        TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(16, y, width, Theme.Small.Height + 2), Theme.Muted, flags);
    }
}

/// <summary>A Swing in the chart's Swing lane: a pill tinted pink (Growth) or lime (Release), filled while its drawer is open.</summary>
internal sealed class SwingChip : Button
{
    private bool _hover;
    private bool _selected;

    public SwingChip(SwingRow swing, string text)
    {
        Swing = swing;
        Text = text;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.BodyStrong;
        Margin = new Padding(0, 0, 6, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        var measured = TextRenderer.MeasureText(text, Font);
        Size = new Size(measured.Width + 22, Math.Max(28, measured.Height + 10));
        AccessibleName = $"Open the {(swing.Kind == "growth" ? "Growth" : "Release")} Swing: {text}";
    }

    public SwingRow Swing { get; }

    private Color Kind => Swing.Kind == "growth" ? Theme.Growth : Theme.Release;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = Parent?.BackColor ?? Theme.Card;
        var fill = _selected ? Kind : Tint.Over(Kind, back, _hover ? 0.24 : 0.13);
        Tint.Card(g, this, new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2, fill, _selected ? Kind : Tint.Over(Kind, back, 0.5));
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _selected ? Theme.Ground : Kind,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Text) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 3, 3, Width - 7, Height - 7);
        }
    }
}

/// <summary>
/// Where space is, as tiles sized by GB: the biggest Probes on a tall first row, the next ones below, the rest folded
/// into one "more" tile. Clicking a tile raises <see cref="TileClicked"/> with its Probe's id (the list selects it).
/// </summary>
internal sealed class ProbeTiles : Control
{
    private const int SmallTilesMax = 8;
    private int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
    private int TopRow => S(52);
    private int BottomRow => S(34);
    private int Gap => S(5);
    private int MinSmall => S(114);
    private readonly List<(ProbeSize Probe, string Title)> _items = [];
    private readonly List<(Rectangle Rect, ProbeSize? Probe, long? ClickId, string Title, string Size, string Tip)> _tiles = [];
    private readonly ToolTip _tips = new() { InitialDelay = 250 };
    private long? _selected;
    private int _hover = -1;
    private string _lastTip = "";

    public ProbeTiles()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Probes as tiles sized by GB";
    }

    public event Action<long>? TileClicked;

    /// <summary>One row of small tiles only (a short window), instead of the tall top row plus the small one.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value)
                return;
            _compact = value;
            Arrange();
            Invalidate();
        }
    }

    private bool _compact;

    /// <summary>The height the rows need, in device pixels: two rows, or one when <paramref name="compact"/>.</summary>
    public int Wanted(bool compact) => compact ? Gap + BottomRow + Gap : Gap + TopRow + Gap + BottomRow + Gap;

    public void SetItems(IEnumerable<(ProbeSize Probe, string Title)> items)
    {
        _items.Clear();
        _items.AddRange(items.Where(i => i.Probe.SizeBytes > 0).OrderByDescending(i => i.Probe.SizeBytes));
        Arrange();
        Invalidate();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public long? SelectedId
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    /// <summary>
    /// Top row: the Probes holding at least a fifth of the total (one to three of them). Bottom row: the next ones in
    /// proportion, each at least <see cref="MinSmall"/> px wide, and a "more" tile for whatever doesn't fit.
    /// </summary>
    private void Arrange()
    {
        _tiles.Clear();
        var width = ClientSize.Width;
        if (_items.Count == 0 || width <= 0)
            return;
        var total = Math.Max(1, _items.Sum(i => i.Probe.SizeBytes));
        var top = _compact ? [] : _items.TakeWhile((i, n) => n == 0 || (n < 3 && i.Probe.SizeBytes >= total / 5)).ToList();
        var rest = _items.Skip(top.Count).ToList();
        if (top.Count > 0)
            Row(top, Gap, TopRow, width, more: []);

        if (rest.Count == 0)
            return;
        // Fit as many as can each get MinSmall px, keeping room for the "more" tile when some are left over.
        var fits = Math.Min(rest.Count, SmallTilesMax);
        while (fits > 0)
        {
            var leftover = rest.Count - fits;
            var slots = fits + (leftover > 0 ? 1 : 0);
            if (slots * MinSmall + (slots - 1) * Gap <= width)
                break;
            fits--;
        }
        Row(rest.Take(fits).ToList(), top.Count > 0 ? Gap + TopRow + Gap : Gap, BottomRow, width, rest.Skip(fits).ToList());
    }

    private void Row(List<(ProbeSize Probe, string Title)> row, int y, int height, int width, List<(ProbeSize Probe, string Title)> more)
    {
        var weights = row.Select(i => (double)i.Probe.SizeBytes).ToList();
        if (more.Count > 0)
            weights.Add(Math.Max(more.Sum(m => (double)m.Probe.SizeBytes), weights.DefaultIfEmpty(1).Min() * 0.6));
        if (weights.Count == 0)
            return;
        var free = width - ((weights.Count - 1) * Gap);
        var min = height == TopRow ? 0 : MinSmall;
        // Give every tile its minimum, then share the remainder by size.
        var extra = Math.Max(0, free - (min * weights.Count));
        var sum = Math.Max(1, weights.Sum());
        var x = 0;
        for (var n = 0; n < weights.Count; n++)
        {
            var w = n == weights.Count - 1 ? width - x : min + (int)(extra * weights[n] / sum);
            var rect = new Rectangle(x, y, Math.Max(1, w), height);
            if (n < row.Count)
            {
                var sizeText = SizeText.Gb(row[n].Probe.SizeBytes);
                _tiles.Add((rect, row[n].Probe, row[n].Probe.Id, row[n].Title, sizeText, $"{row[n].Title}: {sizeText}"));
            }
            else
            {
                var moreSum = SizeText.Gb(more.Sum(m => m.Probe.SizeBytes));
                var moreTip = string.Join("\n", more.Select(m => $"{m.Title}: {SizeText.Gb(m.Probe.SizeBytes)}"));
                _tiles.Add((rect, null, more[0].Probe.Id, $"{more.Count} more", moreSum, moreTip));
            }
            x += w + Gap;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _tiles.FindIndex(t => t.Rect.Contains(e.Location));
        Cursor = hit >= 0 && _tiles[hit].ClickId is not null ? Cursors.Hand : Cursors.Default;
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
        var tip = hit >= 0 ? _tiles[hit].Tip : "";
        if (tip != _lastTip)
        {
            _lastTip = tip;
            _tips.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_tiles.FirstOrDefault(t => t.Rect.Contains(e.Location)).ClickId is { } clickId)
            TileClicked?.Invoke(clickId);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        for (var n = 0; n < _tiles.Count; n++)
        {
            var (rect, probe, clickId, title, size, _) = _tiles[n];
            var big = rect.Height == TopRow && !_compact;
            var selected = probe is not null && probe.Id == _selected;
            var fill = selected ? Theme.Selected : n == _hover && clickId is not null ? Theme.Hover : big ? Tint.Over(Theme.Fill, Theme.Card, 0.22) : Theme.Raised;
            using (var path = CardPanel.Rounded(rect with { Width = rect.Width - 1, Height = rect.Height - 1 }, 8))
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
                using var pen = new Pen(selected ? Theme.SelectedEdge : n == _hover && clickId is not null ? Theme.SelectedEdge : probe is null ? Theme.Hairline : Theme.StrongEdge);
                g.DrawPath(pen, path);
            }
            var inner = Rectangle.Inflate(rect, -10, 0);
            if (big)
            {
                TextRenderer.DrawText(g, title, Theme.BodyStrong, new Rectangle(inner.X, rect.Y + 6, inner.Width, Theme.BodyStrong.Height), Theme.Text, flags);
                TextRenderer.DrawText(g, size, Theme.ForecastValue, new Rectangle(inner.X, rect.Bottom - Theme.ForecastValue.Height - 5, inner.Width, Theme.ForecastValue.Height), Theme.Text, flags);
            }
            else
            {
                // One line when it fits ("npm cache  2.7 GB"), else the size alone.
                var sizeWidth = TextRenderer.MeasureText(size, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                var titleRoom = inner.Width - sizeWidth - 8;
                var line = new Rectangle(inner.X, rect.Y, inner.Width, rect.Height);
                if (titleRoom > 30)
                    TextRenderer.DrawText(g, title, Theme.Small, line with { Width = titleRoom }, probe is null ? Theme.Muted : Theme.Text, flags | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, size, Theme.Small, line, Theme.Muted, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Conditional callout shown on the "Where space is" tab when virtual-disk Probes (WSL, Docker <c>.vhdx</c> files) are
/// large or growing. Explains why <c>.vhdx</c> files grow steadily without triggering Swings and provides one-click
/// copyable reclaim step commands from <c>docs/space-findings.md</c>.
/// </summary>
internal sealed class VirtualDiskBanner : Control
{
    private readonly List<(Rectangle Rect, ReclaimStep Step, int Index)> _chips = [];
    private readonly ToolTip _tips = new() { InitialDelay = 250 };
    private VirtualDiskCallout? _callout;
    private bool _narrow;
    private int _hoverChip = -1;
    private int _copiedChip = -1;
    private string _lastTip = "";

    public VirtualDiskBanner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Alert;
    }

    public event Action<string>? CommandClicked;

    /// <summary>Height needed for the two-line callout card in device pixels.</summary>
    public int WantedHeight => 6 + Math.Max(20, Theme.BodyStrong.Height) + 5 + (Theme.MonoSmall.Height + 8) + 7;

    public void Set(VirtualDiskCallout? callout, bool narrow)
    {
        _callout = callout;
        _narrow = narrow;
        AccessibleName = callout is null ? "" : $"{callout.Headline}. {callout.Explanation}";
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _chips.FindIndex(c => c.Rect.Contains(e.Location));
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        if (hit != _hoverChip)
        {
            _hoverChip = hit;
            Invalidate();
        }
        var tip = hit >= 0
            ? $"{_chips[hit].Step.Command} — {_chips[hit].Step.Detail}\nClick to copy command"
            : _callout?.Explanation ?? "";
        if (tip != _lastTip)
        {
            _lastTip = tip;
            _tips.SetToolTip(this, tip);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverChip != -1)
        {
            _hoverChip = -1;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
            return;
        var hit = _chips.FindIndex(c => c.Rect.Contains(e.Location));
        if (hit < 0)
            return;
        var step = _chips[hit];
        CommandClicked?.Invoke(step.Step.Command);
        _copiedChip = step.Index;
        Invalidate();
        var reset = new System.Windows.Forms.Timer { Interval = 1800 };
        reset.Tick += (_, _) =>
        {
            reset.Dispose();
            if (_copiedChip == step.Index)
            {
                _copiedChip = -1;
                Invalidate();
            }
        };
        reset.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        _chips.Clear();
        if (_callout is not { } callout || Width <= 0 || Height <= 0)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = CardPanel.Rounded(box, 8))
        {
            using var fill = new SolidBrush(Tint.Over(Theme.Amber, Theme.Card, 0.08));
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.AmberEdge);
            g.DrawPath(pen, path);
        }

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        var topHeight = Math.Max(20, Theme.BodyStrong.Height);
        var y = 6;
        var x = 12;

        // Pill: VIRTUAL DISKS
        const string badge = "VIRTUAL DISKS";
        var badgeSize = TextRenderer.MeasureText(g, badge, Theme.Caption, Size.Empty, flags);
        var pillHeight = Math.Max(18, badgeSize.Height + 5);
        var pill = new Rectangle(x, y + ((topHeight - pillHeight) / 2), badgeSize.Width + 14, pillHeight);
        using (var pillPath = CardPanel.Rounded(pill, 8))
        using (var pillFill = new SolidBrush(Theme.AmberGround))
            g.FillPath(pillFill, pillPath);
        TextRenderer.DrawText(g, badge, Theme.Caption, pill, Theme.Amber, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        x = pill.Right + 10;

        // Headline
        var headWidth = TextRenderer.MeasureText(g, callout.Headline, Theme.BodyStrong, Size.Empty, flags).Width;
        var headRoom = Math.Max(0, box.Right - 12 - x);
        TextRenderer.DrawText(g, callout.Headline, Theme.BodyStrong, new Rectangle(x, y, headRoom, topHeight), Theme.Text, flags | TextFormatFlags.EndEllipsis);
        x += headWidth + 8;

        // Steady-growth note when there is room
        if (!_narrow && x < box.Right - 80)
        {
            const string why = "·  .vhdx files grow and never shrink on their own (steady growth, not Swings)";
            TextRenderer.DrawText(g, why, Theme.Small, new Rectangle(x, y, box.Right - 12 - x, topHeight), Theme.Muted, flags | TextFormatFlags.EndEllipsis);
        }

        // Second line: numbered reclaim steps with clickable command chips
        y += topHeight + 5;
        x = 12;
        var chipHeight = Theme.MonoSmall.Height + 8;
        var fullNeeded = callout.Steps.Select((s, idx) =>
            TextRenderer.MeasureText(g, $"{idx + 1}. {s.Label}:", Theme.Small, Size.Empty, flags).Width + 6
            + TextRenderer.MeasureText(g, s.Command, Theme.MonoSmall, Size.Empty, flags).Width + 16).Sum()
            + (Math.Max(0, callout.Steps.Count - 1) * 14);
        var showStepLabels = fullNeeded <= box.Right - 24;
        for (var i = 0; i < callout.Steps.Count; i++)
        {
            var step = callout.Steps[i];
            var prefix = showStepLabels ? $"{i + 1}. {step.Label}:" : $"{i + 1}.";
            var prefixWidth = TextRenderer.MeasureText(g, prefix, Theme.Small, Size.Empty, flags).Width;
            if (x + prefixWidth + 60 > box.Right - 12)
                break;
            TextRenderer.DrawText(g, prefix, Theme.Small, new Rectangle(x, y, prefixWidth, chipHeight), Theme.Muted, flags);
            x += prefixWidth + 6;

            var copied = _copiedChip == i;
            var cmdText = copied ? "Copied" : step.Command;
            var cmdWidth = TextRenderer.MeasureText(g, cmdText, Theme.MonoSmall, Size.Empty, flags).Width;
            var chipWidth = Math.Min(box.Right - 12 - x, cmdWidth + 16);
            if (chipWidth < 40)
                break;
            var chip = new Rectangle(x, y, chipWidth, chipHeight);
            _chips.Add((chip, step, i));
            using (var chipPath = CardPanel.Rounded(chip, 5))
            {
                using var chipFill = new SolidBrush(i == _hoverChip ? Theme.Hover : Theme.Ground);
                g.FillPath(chipFill, chipPath);
                using var chipPen = new Pen(i == _hoverChip ? Theme.SelectedEdge : Theme.Hairline);
                g.DrawPath(chipPen, chipPath);
            }
            TextRenderer.DrawText(g, cmdText, Theme.MonoSmall, Rectangle.Inflate(chip, -8, 0), copied ? Theme.Lime : Theme.Text, flags | TextFormatFlags.EndEllipsis);
            x = chip.Right + (showStepLabels ? 14 : 10);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }
}
