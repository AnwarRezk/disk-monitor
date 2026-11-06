namespace DiskDrift.Tray.Dashboard;

/// <summary>One line of text in several colours and weights, drawn as a single run (no gaps between labels).</summary>
internal sealed class RichLine : Control
{
    private (string Text, Color Color, Font Font)[] _parts = [];

    public RichLine()
    {
        BackColor = Theme.Card;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetParts(params (string Text, Color Color, Font Font)[] parts)
    {
        _parts = parts;
        Text = string.Concat(parts.Select(p => p.Text)); // for screen readers
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        var x = 0;
        foreach (var (text, color, font) in _parts)
        {
            var width = TextRenderer.MeasureText(e.Graphics, text, font, new Size(int.MaxValue, Height), flags).Width;
            TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(x, 0, Math.Max(0, Width - x), Height), color, flags | TextFormatFlags.EndEllipsis);
            x += width;
            if (x >= Width)
                break;
        }
    }
}
