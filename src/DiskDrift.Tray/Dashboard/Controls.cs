using System.ComponentModel;
using System.Drawing.Drawing2D;
using DiskDrift.Core.Native;

namespace DiskDrift.Tray.Dashboard;

/// <summary>A panel with the card fill and a 1 px hairline, rounded corners drawn on the parent's ground.</summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        BackColor = Theme.Card;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Ground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rect, Radius);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
        using var pen = new Pen(Theme.Hairline);
        e.Graphics.DrawPath(pen, path);
    }

    /// <summary>Corner radius in pixels.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 12;

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Shared UI-thread animation ticker (~60 fps while active, zero timers running when idle).
/// Automatically disabled when Windows client-area animations are turned off or during <c>--screenshot</c> runs.
/// </summary>
internal static class UiMotion
{
    public static bool Suppress { get; set; }

    public static bool Enabled => !Suppress && SystemInformation.UIEffectsEnabled && !SystemInformation.TerminalServerSession;

    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 16 };
    private static readonly List<Func<bool>> Active = [];
    private static bool _wired;

    public static void Start(Func<bool> tick)
    {
        if (!Enabled)
            return;
        if (!_wired)
        {
            _wired = true;
            Timer.Tick += (_, _) =>
            {
                for (var i = Active.Count - 1; i >= 0; i--)
                {
                    if (!Active[i]())
                        Active.RemoveAt(i);
                }
                if (Active.Count == 0)
                    Timer.Stop();
            };
        }
        if (!Active.Contains(tick))
            Active.Add(tick);
        if (!Timer.Enabled)
            Timer.Start();
    }

    public static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0) - 1.0;
        return (t * t * t) + 1.0;
    }

    public static bool StepTowards(ref double current, double target, double step)
    {
        if (!Enabled || Math.Abs(target - current) <= step)
        {
            current = target;
            return false;
        }
        current += Math.Sign(target - current) * step;
        return true;
    }
}

/// <summary>
/// A tab strip <see cref="FlowLayoutPanel"/> that draws the bottom hairline and animates the 2 px pink active-tab
/// indicator smoothly between tabs.
/// </summary>
internal sealed class TabStripPanel : FlowLayoutPanel
{
    private double _fromX, _fromW, _toX, _toW, _curX, _curW;
    private long _animStartTicks;
    private bool _initialized;

    public TabStripPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public (int X, int Width) Indicator
    {
        get
        {
            if (_initialized && _curW > 0)
                return ((int)Math.Round(_curX), (int)Math.Round(_curW));
            if (Controls.OfType<ToggleButton>().FirstOrDefault(b => b.Selected) is { Width: > 0 } selected)
                return (selected.Left, selected.Width);
            return (0, 0);
        }
    }

    public void MoveIndicatorTo(Control tab, bool animate = true)
    {
        if (tab.Width <= 0)
            return;
        _toX = tab.Left;
        _toW = tab.Width;
        if (!_initialized || !animate || !UiMotion.Enabled || !IsHandleCreated)
        {
            _initialized = true;
            (_fromX, _fromW, _curX, _curW) = (_toX, _toW, _toX, _toW);
            InvalidateIndicator();
            return;
        }
        if (Math.Abs(_curX - _toX) < 0.5 && Math.Abs(_curW - _toW) < 0.5)
            return;
        _fromX = _curX;
        _fromW = _curW;
        _animStartTicks = Environment.TickCount64;
        UiMotion.Start(StepIndicator);
    }

    private bool StepIndicator()
    {
        if (IsDisposed || !IsHandleCreated)
            return false;
        var t = (Environment.TickCount64 - _animStartTicks) / 150.0;
        var eased = UiMotion.EaseOutCubic(t);
        _curX = _fromX + ((_toX - _fromX) * eased);
        _curW = _fromW + ((_toW - _fromW) * eased);
        InvalidateIndicator();
        return t < 1.0;
    }

    private void InvalidateIndicator()
    {
        Invalidate(new Rectangle(0, Math.Max(0, Height - 4), Width, 4));
        foreach (Control c in Controls)
            c.Invalidate(new Rectangle(0, Math.Max(0, c.Height - 4), c.Width, 4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Hairline))
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        var (ix, iw) = Indicator;
        if (iw > 0)
        {
            using var underline = new SolidBrush(Theme.Line);
            e.Graphics.FillRectangle(underline, ix, Math.Max(0, Height - 3), iw, 2);
        }
    }
}

internal enum ToggleLook
{
    /// <summary>A Volume tab in the header band: outlined violet and filled when selected, plain text otherwise.</summary>
    Chip,

    /// <summary>A range pill: rounded, outlined, filled violet when selected.</summary>
    Segment,

    /// <summary>A tab with a pink underline when selected.</summary>
    Tab,

    /// <summary>A secondary command: outlined violet (Open location).</summary>
    Action,

    /// <summary>The primary command: filled violet, white text (Probe now).</summary>
    Primary,
}

/// <summary>A real <see cref="Button"/> (focusable, keyboard and screen-reader friendly) painted in the dashboard's look.</summary>
internal sealed class ToggleButton : Button
{
    private bool _selected;
    private bool _hover;
    private bool _pressed;
    private double _hoverMix;
    private double _selectMix;
    private double _pressMix;

    public ToggleButton(string text, ToggleLook look)
    {
        Text = text;
        Look = look;
        AutoSize = false;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Margin = look switch
        {
            ToggleLook.Segment => new Padding(0, 0, 4, 0),
            ToggleLook.Tab => new Padding(0, 0, 8, 0),
            _ => new Padding(0, 0, 6, 0),
        };
        Font = look switch { ToggleLook.Tab => Theme.Tab, ToggleLook.Primary => Theme.BodyStrong, _ => Theme.Body };
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = Measure();
    }

    public ToggleLook Look { get; }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = Measure();
        Invalidate();
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            AccessibleDescription = value ? "selected" : null;
            AnimateState();
        }
    }

    private Size Measure()
    {
        var text = TextRenderer.MeasureText(Text, Look == ToggleLook.Segment ? Theme.BodyStrong : Font);
        return Look switch
        {
            ToggleLook.Tab => new Size(text.Width + 24, Math.Max(42, text.Height + 22)),
            ToggleLook.Segment => new Size(Math.Max(46, text.Width + 22), Math.Max(32, text.Height + 13)),
            ToggleLook.Action or ToggleLook.Primary => new Size(text.Width + 30, Math.Max(34, text.Height + 15)),
            _ => new Size(text.Width + 24, Math.Max(34, text.Height + 15)),
        };
    }

    private void AnimateState()
    {
        if (!UiMotion.Enabled || !IsHandleCreated || !Visible)
        {
            _hoverMix = _hover ? 1.0 : 0.0;
            _selectMix = _selected ? 1.0 : 0.0;
            _pressMix = _pressed ? 1.0 : 0.0;
            Invalidate();
            return;
        }
        UiMotion.Start(StepState);
    }

    private bool StepState()
    {
        if (IsDisposed || !IsHandleCreated)
            return false;
        var h = UiMotion.StepTowards(ref _hoverMix, _hover ? 1.0 : 0.0, 0.16);
        var s = UiMotion.StepTowards(ref _selectMix, _selected ? 1.0 : 0.0, 0.14);
        var p = UiMotion.StepTowards(ref _pressMix, _pressed ? 1.0 : 0.0, 0.25);
        Invalidate();
        return h || s || p;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; AnimateState(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; AnimateState(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            AnimateState();
        }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        if (_pressed)
        {
            _pressed = false;
            AnimateState();
        }
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var back = Parent?.BackColor ?? Theme.Card;
        g.Clear(back);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var textBlend = Math.Max(_selectMix, _hoverMix * 0.85);
        var fore = Tint.Over(Theme.Text, Theme.Muted, textBlend);

        switch (Look)
        {
            case ToggleLook.Chip:
                if (_selectMix > 0.01 || _hoverMix > 0.01)
                {
                    using var path = CardPanel.Rounded(rect, 8);
                    var chipFill = Tint.Over(Theme.Hover, Tint.Over(Theme.RowHover, back, _hoverMix), _selectMix);
                    var chipEdge = Tint.Over(Theme.SelectedEdge, Tint.Over(Theme.Hairline, back, _hoverMix), _selectMix);
                    using var fill = new SolidBrush(chipFill);
                    g.FillPath(fill, path);
                    using var pen = new Pen(chipEdge);
                    g.DrawPath(pen, path);
                }
                break;
            case ToggleLook.Segment:
                using (var path = CardPanel.Rounded(rect, rect.Height / 2))
                {
                    var segFill = Tint.Over(Theme.Selected, Tint.Over(Theme.Hover, back, _hoverMix), _selectMix);
                    if (_pressMix > 0.01)
                        segFill = Tint.Over(Theme.Ground, segFill, _pressMix * 0.25);
                    var segEdge = Tint.Over(Theme.SelectedEdge, Tint.Over(Theme.SelectedEdge, Theme.StrongEdge, _hoverMix * 0.45), _selectMix);
                    using var fill = new SolidBrush(segFill);
                    g.FillPath(fill, path);
                    using var pen = new Pen(segEdge);
                    g.DrawPath(pen, path);
                }
                break;
            case ToggleLook.Action:
                using (var path = CardPanel.Rounded(rect, 8))
                {
                    var actFill = !Enabled ? back : Tint.Over(Theme.Hover, back, _hoverMix);
                    if (_pressMix > 0.01 && Enabled)
                        actFill = Tint.Over(Theme.Selected, actFill, _pressMix * 0.35);
                    var actEdge = !Enabled ? Theme.Hairline : Tint.Over(Theme.AccentHover, Theme.SelectedEdge, _hoverMix);
                    using var fill = new SolidBrush(actFill);
                    g.FillPath(fill, path);
                    using var pen = new Pen(actEdge);
                    g.DrawPath(pen, path);
                }
                fore = Enabled ? Theme.Text : Theme.Faint;
                break;
            case ToggleLook.Primary:
                using (var path = CardPanel.Rounded(rect, 8))
                {
                    var priFill = !Enabled ? Theme.Hover : Tint.Over(Theme.AccentHover, Theme.Accent, _hoverMix);
                    if (_pressMix > 0.01 && Enabled)
                        priFill = Tint.Over(Theme.Selected, priFill, _pressMix * 0.35);
                    using var fill = new SolidBrush(priFill);
                    g.FillPath(fill, path);
                    if (Enabled && _hoverMix > 0.01)
                    {
                        using var glow = new Pen(Tint.Over(Color.White, priFill, _hoverMix * 0.28));
                        g.DrawPath(glow, path);
                    }
                }
                fore = Enabled ? Color.White : Theme.Muted;
                break;
            case ToggleLook.Tab:
                if (Parent is TabStripPanel strip)
                {
                    var (ix, iw) = strip.Indicator;
                    if (iw > 0)
                    {
                        using var underline = new SolidBrush(Theme.Line);
                        g.FillRectangle(underline, ix - Left, Height - 2, iw, 2);
                    }
                }
                else if (_selectMix > 0.01)
                {
                    using var underline = new SolidBrush(Tint.Over(Theme.Line, back, _selectMix));
                    g.FillRectangle(underline, 0, Height - 2, Width, 2);
                }
                break;
        }

        var font = _selected && Look == ToggleLook.Segment ? Theme.BodyStrong : Font;
        var textRect = _pressMix > 0.25 && Look != ToggleLook.Tab ? ClientRectangle with { Y = ClientRectangle.Y + 1 } : ClientRectangle;
        TextRenderer.DrawText(g, Text, font, textRect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Tabs show focus as a faint underline: a dotted box around a just-clicked tab looked like a glitch.
        if (Focused && ShowFocusCues && Look == ToggleLook.Tab && !_selected)
        {
            using var underline = new SolidBrush(Theme.SelectedEdge);
            g.FillRectangle(underline, 0, Height - 2, Width, 2);
        }
        else if (Focused && ShowFocusCues && Look != ToggleLook.Tab)
        {
            using var focus = new Pen(Look == ToggleLook.Primary ? Color.White : Theme.SelectedEdge) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 3, 3, Width - 7, Height - 7);
        }
    }
}

/// <summary>
/// A details <see cref="ListView"/> drawn in the dashboard's look: uppercase muted headers, hairline row dividers,
/// a violet selection, and an optional custom painter per column (Top Writers draws its bar that way).
/// </summary>
internal sealed class DarkListView : ListView
{
    private readonly Label _empty = new()
    {
        AutoSize = false, Visible = false, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Theme.Muted, BackColor = Theme.Card, Font = Theme.Body,
    };
    private int _hoverIndex = -1;

    public DarkListView(int rowHeight = 0)
    {
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        OwnerDraw = true;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        var actualRowHeight = rowHeight > 0 ? rowHeight : Math.Max(30, Theme.Body.Height + 12);
        // A 1 px wide image list is the standard way to set a details ListView's row height.
        SmallImageList = new ImageList { ImageSize = new Size(1, actualRowHeight) };
        DoubleBuffered = true;
        Controls.Add(_empty);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _empty.BackColor = BackColor;
    }

    /// <summary>Left inset in pixels for the first column, so the list aligns with card headers above it.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LeadingPadding { get; set; } = 15;

    /// <summary>Shown just under the column headers while the list has no rows.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText
    {
        get => _empty.Text;
        set => _empty.Text = value;
    }

    /// <summary>Shows or hides the empty note; call after filling the list.</summary>
    public void UpdateEmpty()
    {
        // Rows may have brought up a vertical scroll bar: re-fit the columns so no horizontal one appears, again once
        // the list has laid out its scroll bars.
        FillLastColumn();
        if (IsHandleCreated)
            BeginInvoke(FillLastColumn);
        _empty.Visible = Items.Count == 0 && EmptyText.Length > 0;
        PlaceEmpty();
    }

    private void PlaceEmpty()
    {
        var headerHeight = HeaderStyle == ColumnHeaderStyle.None ? 0 : Math.Max(30, Theme.Caption.Height + 16);
        _empty.Bounds = new Rectangle(0, headerHeight + 6, ClientSize.Width, Math.Min(56, Math.Max(0, ClientSize.Height - headerHeight - 6)));
    }

    /// <summary>When set, each column's <see cref="ColumnHeader.Tag"/> (an int) is its share of the width, kept on resize.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ProportionalColumns { get; set; }

    /// <summary>Paints a cell itself and returns true, or returns false to get the default text cell.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Graphics, Rectangle, ListViewItem, int, bool>? CellPainter { get; set; }

    /// <summary>Indexes of columns holding file paths, shortened in the middle so the file name stays visible.</summary>
    public HashSet<int> PathColumns { get; } = [];

    /// <summary>Indexes of columns drawn in the monospace font.</summary>
    public HashSet<int> MonoColumns { get; } = [];

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UxTheme.TryUseDarkTheme(Handle);
    }

    // The columns always fill the width exactly, so a horizontal scroll bar could only ever come from rounding or a
    // vertical bar arriving before the re-fit: drop its style whenever the list sizes its frame.
    protected override void WndProc(ref Message m)
    {
        const int WmNcCalcSize = 0x0083;
        if (m.Msg == WmNcCalcSize && ProportionalColumns)
            User32.HideHorizontalScrollBar(m.HWnd);
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FillLastColumn();
        PlaceEmpty();
    }

    // A vertical scroll bar appearing or going shrinks the client area without a resize: re-fit the columns then too.
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FillLastColumn();
    }

    /// <summary>Stretches the last column to the right edge, so no unpainted header area is left over.</summary>
    public void FillLastColumn()
    {
        if (Columns.Count == 0 || ClientSize.Width <= 0)
            return;
        if (ProportionalColumns)
        {
            // A positive Tag is a share of the width; a negative one is a fixed width in pixels.
            var total = Math.Max(1, Columns.Cast<ColumnHeader>().Sum(c => c.Tag is int share and > 0 ? share : 0));
            var fixedWidth = Columns.Cast<ColumnHeader>().Sum(c => c.Tag is int px and < 0 ? -px : 0);
            // ClientSize already excludes a vertical scroll bar; the horizontal one is suppressed in WndProc.
            var width = ClientSize.Width;
            var flexible = Math.Max(0, width - fixedWidth);
            foreach (var c in Columns.Cast<ColumnHeader>().Take(Columns.Count - 1))
                c.Width = c.Tag is int px and < 0 ? -px : Math.Max(48, flexible * (c.Tag is int share ? share : 0) / total);
            // The last column takes the exact remainder: no unpainted header strip, no horizontal scroll bar.
            Columns[^1].Width = Math.Max(48, width - Columns.Cast<ColumnHeader>().Take(Columns.Count - 1).Sum(c => c.Width));
            return;
        }
        var others = Columns.Cast<ColumnHeader>().Take(Columns.Count - 1).Sum(c => c.Width);
        var last = Columns[^1];
        last.Width = Math.Max(120, ClientSize.Width - others);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location).Item?.Index ?? -1;
        SetHoverIndex(hit);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverIndex(-1);
    }

    private void SetHoverIndex(int index)
    {
        if (_hoverIndex == index)
            return;
        var prev = _hoverIndex;
        _hoverIndex = index;
        if (prev >= 0 && prev < Items.Count)
            Invalidate(Items[prev].Bounds);
        if (index >= 0 && index < Items.Count)
            Invalidate(Items[index].Bounds);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        var leftPad = e.ColumnIndex == 0 ? LeadingPadding : 12;
        var text = new Rectangle(e.Bounds.X + leftPad, e.Bounds.Y, Math.Max(0, e.Bounds.Width - leftPad - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text.ToUpperInvariant(), Theme.Caption, text, Theme.Muted, Flags(e.Header?.TextAlign ?? HorizontalAlignment.Left));
        using var pen = new Pen(Theme.Hairline);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e) { }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
            return;
        var selected = e.Item.Selected;
        var hovered = e.Item.Index == _hoverIndex;
        using (var back = new SolidBrush(selected ? Theme.Selected : hovered ? Theme.RowHover : BackColor))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (e.Item.Index > 0 || HeaderStyle == ColumnHeaderStyle.None)
        {
            using var divider = new Pen(Theme.Grid);
            e.Graphics.DrawLine(divider, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);
        }

        var leftPad = e.ColumnIndex == 0 ? LeadingPadding : 12;
        var cell = new Rectangle(e.Bounds.X + leftPad, e.Bounds.Y, Math.Max(0, e.Bounds.Width - leftPad - 12), e.Bounds.Height);
        if (CellPainter?.Invoke(e.Graphics, cell, e.Item, e.ColumnIndex) == true)
            return;

        var mono = MonoColumns.Contains(e.ColumnIndex);
        var color = e.SubItem.ForeColor == ForeColor || e.SubItem.ForeColor.IsEmpty
            ? (selected ? Theme.Text : mono ? Theme.Muted : Theme.Text)
            : e.SubItem.ForeColor;
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, mono ? Theme.Mono : Font, cell, color,
            Flags(Columns[e.ColumnIndex].TextAlign) | (PathColumns.Contains(e.ColumnIndex) ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis));
    }

    private static TextFormatFlags Flags(HorizontalAlignment align) =>
        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | align switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };
}

/// <summary>The chart's hover box: a time and a value, drawn as a small violet card that ignores the mouse.</summary>
internal sealed class TipBox : Control
{
    private string _top = "";
    private string _main = "";
    private string _sub = "";

    public TipBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
    }

    /// <summary>Sets the time, the value and an optional third line (the Swing under the cursor).</summary>
    public void Set(string top, string main, string sub = "")
    {
        (_top, _main, _sub) = (top, main, sub);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var w = Math.Max(TextRenderer.MeasureText(top, Theme.MonoSmall, Size.Empty, flags).Width, TextRenderer.MeasureText(main, Theme.BodyStrong, Size.Empty, flags).Width);
        if (sub.Length > 0)
            w = Math.Max(w, TextRenderer.MeasureText(sub, Theme.Small, Size.Empty, flags).Width);
        var nextSize = new Size(w + 24, 8 + Theme.MonoSmall.Height + 2 + Theme.BodyStrong.Height + (sub.Length > 0 ? 2 + Theme.Small.Height : 0) + 8);
        if (Size != nextSize)
        {
            Size = nextSize;
            using var regionPath = CardPanel.Rounded(new Rectangle(0, 0, Width, Height), 8);
            var oldRegion = Region;
            Region = new Region(regionPath);
            oldRegion?.Dispose();
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Region?.Dispose();
        base.Dispose(disposing);
    }

    // Let the chart keep getting mouse moves while the box sits under the cursor.
    protected override void WndProc(ref Message m)
    {
        const int WmNcHitTest = 0x84, HtTransparent = -1;
        if (m.Msg == WmNcHitTest)
        {
            m.Result = HtTransparent;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Raised);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8))
        {
            using var pen = new Pen(Theme.StrongEdge);
            g.DrawPath(pen, path);
        }
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var y = 8;
        TextRenderer.DrawText(g, _top, Theme.MonoSmall, new Rectangle(12, y, Width - 24, Theme.MonoSmall.Height + 2), Theme.Muted, flags);
        y += Theme.MonoSmall.Height + 2;
        TextRenderer.DrawText(g, _main, Theme.BodyStrong, new Rectangle(12, y, Width - 24, Theme.BodyStrong.Height), Theme.Text, flags);
        y += Theme.BodyStrong.Height + 2;
        if (_sub.Length > 0)
            TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(12, y, Width - 24, Theme.Small.Height), Theme.Muted, flags);
    }
}

/// <summary>Context-menu colours in the dashboard's look (the Probe and Top Writers menus, and the tray menu).</summary>
internal sealed class MenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Raised;
    public override Color MenuBorder => Theme.StrongEdge;
    public override Color MenuItemBorder => Theme.Hover;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color ImageMarginGradientBegin => Theme.Raised;
    public override Color ImageMarginGradientMiddle => Theme.Raised;
    public override Color ImageMarginGradientEnd => Theme.Raised;
    public override Color SeparatorDark => Theme.Hairline;
    public override Color SeparatorLight => Theme.Raised;
}
