using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskMon.Core.Analysis;
using DiskMon.Core.Native;

namespace DiskMon.Tray;

/// <summary>Draws the tray icon at runtime: a coloured tile with the free-space number on it.</summary>
internal static class TrayIconRenderer
{
    private static readonly Color Green = Color.FromArgb(0x2E, 0x7D, 0x32);
    private static readonly Color Amber = Color.FromArgb(0xF9, 0xA8, 0x25);
    private static readonly Color Red = Color.FromArgb(0xC6, 0x28, 0x28);
    private static readonly Color Grey = Color.FromArgb(0x75, 0x75, 0x75);

    public static Icon Render(FreeSpaceLevel level, string label) => Render(Fill(level), level == FreeSpaceLevel.Amber ? Color.Black : Color.White, label);

    /// <summary>The "no data" icon: grey with a question mark.</summary>
    public static Icon RenderUnknown() => Render(Grey, Color.White, "?");

    /// <summary>The "Collector not running" icon: grey with the last known free-space number.</summary>
    public static Icon RenderStale(string label) => Render(Grey, Color.White, label);

    private static Color Fill(FreeSpaceLevel level) => level switch
    {
        FreeSpaceLevel.Red => Red,
        FreeSpaceLevel.Amber => Amber,
        _ => Green,
    };

    private static Icon Render(Color fill, Color ink, string label)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var path = RoundedRect(new RectangleF(0, 0, size.Width - 1, size.Height - 1), size.Width / 5f))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);

            using var font = FitFont(g, label, size);
            using var inkBrush = new SolidBrush(ink);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(label, font, inkBrush, new RectangleF(0, 0, size.Width, size.Height), format);
        }

        // GetHicon hands back an unmanaged handle that Icon.Dispose() does not free: clone, then destroy it.
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            User32.DestroyIcon(handle);
        }
    }

    /// <summary>The largest bold font (in pixels) whose rendering of <paramref name="label"/> fits the tile.</summary>
    private static Font FitFont(Graphics g, string label, Size size)
    {
        for (var px = size.Height * 0.85f; ; px -= 0.5f)
        {
            var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
            if (px <= 6 || (measured.Width <= size.Width && measured.Height <= size.Height + 2))
                return font;
            font.Dispose();
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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
