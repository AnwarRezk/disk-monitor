using System.ComponentModel;
using System.Drawing.Drawing2D;
using DiskMon.Core.Native;

namespace DiskMon.Tray.Dashboard;

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
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rect, 10);
        using var ground = new Region(ClientRectangle);
        ground.Exclude(path);
        using var groundBrush = new SolidBrush(Parent?.BackColor ?? Theme.Ground);
        e.Graphics.FillRegion(groundBrush, ground);
        using var pen = new Pen(Theme.Hairline);
        e.Graphics.DrawPath(pen, path);
    }

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

internal enum ToggleLook
{
    /// <summary>A Volume chip in the header: outlined, filled violet when selected.</summary>
    Chip,

    /// <summary>A range button inside a segmented control: no outline, filled violet when selected.</summary>
    Segment,

    /// <summary>An uppercase tab with a pink underline when selected.</summary>
    Tab,

    /// <summary>A command button: filled violet with a lighter edge (Probe now).</summary>
    Action,
}

/// <summary>A real <see cref="Button"/> (focusable, keyboard and screen-reader friendly) painted in the dashboard's look.</summary>
internal sealed class ToggleButton : Button
{
    private bool _selected;
    private bool _hover;

    public ToggleButton(string text, ToggleLook look)
    {
        Text = text;
        Look = look;
        AutoSize = false;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Margin = look == ToggleLook.Segment ? Padding.Empty : new Padding(0, 0, look == ToggleLook.Tab ? 28 : 6, 0);
        Font = look == ToggleLook.Tab ? Theme.Caption : Theme.Body;
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
            Invalidate();
        }
    }

    private Size Measure()
    {
        var text = TextRenderer.MeasureText(Look == ToggleLook.Tab ? Text.ToUpperInvariant() : Text, Font);
        return Look switch
        {
            ToggleLook.Tab => new Size(text.Width + 4, 44),
            ToggleLook.Segment => new Size(text.Width + 24, 32),
            ToggleLook.Action => new Size(text.Width + 24, 30),
            _ => new Size(text.Width + 28, 32),
        };
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var back = Parent?.BackColor ?? Theme.Card;
        g.Clear(back);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var fore = _selected ? Theme.Text : Theme.Muted;

        switch (Look)
        {
            case ToggleLook.Chip:
                using (var path = CardPanel.Rounded(rect, 6))
                {
                    using var fill = new SolidBrush(_selected ? Theme.Selected : _hover ? Theme.Hover : back);
                    g.FillPath(fill, path);
                    using var pen = new Pen(_selected ? Theme.SelectedEdge : Theme.Hairline);
                    g.DrawPath(pen, path);
                }
                break;
            case ToggleLook.Segment:
                using (var fill = new SolidBrush(_selected ? Theme.Selected : _hover ? Theme.Hover : back))
                    g.FillRectangle(fill, ClientRectangle);
                break;
            case ToggleLook.Action:
                using (var path = CardPanel.Rounded(rect, 6))
                {
                    using var fill = new SolidBrush(!Enabled ? Theme.Hover : _hover ? Theme.SelectedEdge : Theme.Selected);
                    g.FillPath(fill, path);
                    using var pen = new Pen(Theme.SelectedEdge);
                    g.DrawPath(pen, path);
                }
                fore = Enabled ? Theme.Text : Theme.Muted;
                break;
            case ToggleLook.Tab:
                if (_selected)
                {
                    using var underline = new SolidBrush(Theme.Line);
                    g.FillRectangle(underline, 0, Height - 2, Width, 2);
                }
                else if (_hover)
                {
                    fore = Theme.Text;
                }
                break;
        }

        var label = Look == ToggleLook.Tab ? Text.ToUpperInvariant() : Text;
        var font = _selected && Look == ToggleLook.Segment ? Theme.BodyStrong : Font;
        TextRenderer.DrawText(g, label, font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Tabs show focus as a faint underline: a dotted box around a just-clicked tab looked like a glitch.
        if (Focused && ShowFocusCues && Look == ToggleLook.Tab && !_selected)
        {
            using var underline = new SolidBrush(Theme.SelectedEdge);
            g.FillRectangle(underline, 0, Height - 2, Width, 2);
        }
        else if (Focused && ShowFocusCues && Look != ToggleLook.Tab)
        {
            using var focus = new Pen(Theme.SelectedEdge) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 2, 2, Width - 5, Height - 5);
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

    public DarkListView(int rowHeight = 30)
    {
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        OwnerDraw = true;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        // A 1 px wide image list is the standard way to set a details ListView's row height.
        SmallImageList = new ImageList { ImageSize = new Size(1, rowHeight) };
        DoubleBuffered = true;
        Controls.Add(_empty);
    }

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
        const int headerHeight = 30;
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

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(Theme.Card);
        e.Graphics.FillRectangle(back, e.Bounds);
        var text = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 24, e.Bounds.Height);
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
        using (var back = new SolidBrush(selected ? Theme.Selected : Theme.Card))
            e.Graphics.FillRectangle(back, e.Bounds);
        using (var divider = new Pen(Theme.Grid))
            e.Graphics.DrawLine(divider, e.Bounds.Left, e.Bounds.Top, e.Bounds.Right, e.Bounds.Top);

        var cell = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 24, e.Bounds.Height);
        if (CellPainter?.Invoke(e.Graphics, cell, e.Item, e.ColumnIndex) == true)
            return;

        var mono = MonoColumns.Contains(e.ColumnIndex);
        var color = e.SubItem.ForeColor == ForeColor || e.SubItem.ForeColor.IsEmpty ? (mono ? Theme.Muted : Theme.Text) : e.SubItem.ForeColor;
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

/// <summary>A label drawn without GDI's side padding, so big figures line up flush with the captions above them.</summary>
internal sealed class TightLabel : Label
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public TightLabel()
    {
        AutoSize = true;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>
    /// Ends at the text's baseline (no room below for descenders, which figures and captions don't have), so labels in
    /// different sizes sitting on one bottom edge share a baseline.
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var size = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        var family = Font.FontFamily;
        var ascent = Font.Height * family.GetCellAscent(Font.Style) / (double)family.GetLineSpacing(Font.Style);
        return new Size(size.Width, (int)Math.Ceiling(ascent) + 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, Font.Height), ForeColor, Flags);
    }
}

/// <summary>
/// Lays its children out left to right with a gap, wrapping to new lines when they don't fit, each line aligned on its
/// bottom edge. <see cref="Trailing"/> sits at the right end of the first line when it fits there, otherwise on a line
/// of its own. The panel's height follows its content.
/// </summary>
internal sealed class WrapRow : Panel
{
    public WrapRow()
    {
        BackColor = Theme.Card;
        DoubleBuffered = true;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Gap { get; set; } = 28;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LineGap { get; set; } = 12;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? Trailing { get; set; }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var width = ClientSize.Width;
        if (width <= 0)
            return;
        var items = Controls.Cast<Control>().Where(c => c.Visible && c != Trailing).ToList();
        var lines = new List<List<Control>> { new() };
        var x = 0;
        var trailingWidth = Trailing?.Visible == true ? Trailing.PreferredSize.Width : 0;
        foreach (var c in items)
        {
            var size = c.PreferredSize;
            // The first line keeps room for the trailing control when it can.
            if (lines[^1].Count > 0 && x + size.Width > width)
            {
                lines.Add([]);
                x = 0;
            }
            lines[^1].Add(c);
            x += size.Width + Gap;
        }

        // The trailing control (range buttons) ends the last line when it fits there, else gets a line of its own.
        var lastLineWidth = lines[^1].Sum(c => c.PreferredSize.Width) + (Gap * Math.Max(0, lines[^1].Count - 1));
        var trailingOnLast = Trailing?.Visible == true && lastLineWidth + Gap + trailingWidth <= width;
        var y = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var height = line.Count == 0 ? 0 : line.Max(c => c.PreferredSize.Height);
            var last = i == lines.Count - 1;
            if (last && trailingOnLast)
                height = Math.Max(height, Trailing!.PreferredSize.Height);
            x = 0;
            foreach (var c in line)
            {
                var size = c.PreferredSize;
                c.Bounds = new Rectangle(x, y + height - size.Height, size.Width, size.Height);
                x += size.Width + Gap;
            }
            if (last && trailingOnLast)
            {
                var t = Trailing!.PreferredSize;
                Trailing.Bounds = new Rectangle(width - t.Width, y + height - t.Height, t.Width, t.Height);
            }
            y += height + LineGap;
        }
        if (Trailing?.Visible == true && !trailingOnLast)
        {
            var t = Trailing.PreferredSize;
            Trailing.Bounds = new Rectangle(0, y, t.Width, t.Height);
            y += t.Height + LineGap;
        }
        var wanted = Math.Max(0, y - LineGap) + Padding.Vertical;
        if (Height != wanted)
            Height = wanted;
    }
}

/// <summary>A tinted mini card: caption, a figure and a one-line explanation (the "Full in" forecast).</summary>
internal sealed class NoteCard : Control
{
    private string _caption = "";
    private string _value = "";
    private string _detail = "";
    private Color _accent = Theme.Muted;
    private Color _ground = Theme.Card;
    private Color _edge = Theme.Hairline;

    public NoteCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty;
    }

    public void Set(string caption, string value, string detail, Color accent, Color ground, Color edge)
    {
        (_caption, _value, _detail, _accent, _ground, _edge) = (caption, value, detail, accent, ground, edge);
        AccessibleName = $"{caption} {value}, {detail}";
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
        Parent?.PerformLayout();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var w = Math.Max(TextRenderer.MeasureText(_caption, Theme.Caption, Size.Empty, flags).Width,
            Math.Max(TextRenderer.MeasureText(_value, Theme.ForecastValue, Size.Empty, flags).Width, TextRenderer.MeasureText(_detail, Theme.Small, Size.Empty, flags).Width));
        return new Size(w + 24, 8 + Theme.Caption.Height + 2 + Theme.ForecastValue.Height + 2 + Theme.Small.Height + 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8))
        {
            using var fill = new SolidBrush(_ground);
            g.FillPath(fill, path);
            using var pen = new Pen(_edge);
            g.DrawPath(pen, path);
        }
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        var y = 8;
        TextRenderer.DrawText(g, _caption, Theme.Caption, new Rectangle(12, y, Width - 24, Theme.Caption.Height), _accent, flags);
        y += Theme.Caption.Height + 2;
        TextRenderer.DrawText(g, _value, Theme.ForecastValue, new Rectangle(12, y, Width - 24, Theme.ForecastValue.Height), _accent, flags);
        y += Theme.ForecastValue.Height + 2;
        TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(12, y, Width - 24, Theme.Small.Height), Theme.Muted, flags);
    }
}

/// <summary>The chart's hover box: a time and a value, drawn as a small violet card that ignores the mouse.</summary>
internal sealed class TipBox : Control
{
    private string _top = "";
    private string _main = "";

    public TipBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Visible = false;
    }

    public void Set(string top, string main)
    {
        (_top, _main) = (top, main);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var w = Math.Max(TextRenderer.MeasureText(top, Theme.MonoSmall, Size.Empty, flags).Width, TextRenderer.MeasureText(main, Theme.BodyStrong, Size.Empty, flags).Width);
        Size = new Size(w + 20, 6 + Theme.MonoSmall.Height + 2 + Theme.BodyStrong.Height + 6);
        Invalidate();
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
        g.Clear(Theme.PlotGround);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CardPanel.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
        {
            using var fill = new SolidBrush(Theme.Grid);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.SelectedEdge);
            g.DrawPath(pen, path);
        }
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, _top, Theme.MonoSmall, new Rectangle(10, 6, Width - 20, Theme.MonoSmall.Height), Theme.Muted, flags);
        TextRenderer.DrawText(g, _main, Theme.BodyStrong, new Rectangle(10, 8 + Theme.MonoSmall.Height, Width - 20, Theme.BodyStrong.Height), Theme.Text, flags);
    }
}
