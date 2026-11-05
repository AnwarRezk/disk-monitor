using System.Drawing.Drawing2D;

namespace DiskDrift.Tray;

/// <summary>
/// The DiskDrift logo (assets/logo.svg): a ring gauge on a violet tile. The .ico is embedded for window icons; the
/// 128 px PNG is scaled down for the dashboard header.
/// </summary>
internal static class AppIcons
{
    private static readonly Lazy<Icon> WindowIcon = new(() => new Icon(Open("diskdrift.ico")));
    private static readonly Lazy<Bitmap> Logo128 = new(() => new Bitmap(Open("logo-128.png")));

    /// <summary>The multi-size app icon, for a form's <see cref="Form.Icon"/>. Shared: don't dispose it.</summary>
    public static Icon Window => WindowIcon.Value;

    /// <summary>The logo as a <paramref name="px"/>-pixel square bitmap, scaled with high-quality filtering.</summary>
    public static Bitmap Logo(int px)
    {
        var bitmap = new Bitmap(px, px);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.DrawImage(Logo128.Value, new Rectangle(0, 0, px, px));
        return bitmap;
    }

    private static Stream Open(string name) =>
        typeof(AppIcons).Assembly.GetManifestResourceStream($"DiskDrift.Tray.{name}")
        ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
}
