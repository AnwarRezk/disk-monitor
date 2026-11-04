namespace DiskMon.Tray.Dashboard;

/// <summary>
/// The dashboard's "Violet" look (option B, chosen 2026-11-04): deep violet ground, hairline borders,
/// a pink free-space line, lime for good news and uppercase micro captions.
/// </summary>
internal static class Theme
{
    public static readonly Color Ground = Hex(0x1f1633);
    public static readonly Color Card = Hex(0x150f23);
    public static readonly Color PlotGround = Hex(0x120c1f);
    public static readonly Color Hairline = Hex(0x362d59);
    public static readonly Color Grid = Hex(0x2a2245);
    public static readonly Color Text = Color.White;
    public static readonly Color Muted = Hex(0xbdb8c0);
    public static readonly Color Selected = Hex(0x422082);
    public static readonly Color SelectedEdge = Hex(0x6a5fc1);
    public static readonly Color Line = Hex(0xfa7faa);
    public static readonly Color Fill = Hex(0x6a5fc1);
    public static readonly Color Lime = Hex(0xc2ef4e);
    public static readonly Color Growth = Hex(0xfa7faa);
    public static readonly Color Release = Hex(0xc2ef4e);
    public static readonly Color Hover = Hex(0x2a2245);
    public static readonly Color RowHover = Hex(0x1c1530);
    public static readonly Color Faint = Hex(0x8d86a0);
    public static readonly Color Amber = Hex(0xffc04d);
    public static readonly Color AmberGround = Hex(0x2a1a24);
    public static readonly Color AmberEdge = Hex(0x5a3a2a);
    public static readonly Color Red = Hex(0xff5c7a);
    public static readonly Color RedGround = Hex(0x2e1424);
    public static readonly Color RedEdge = Hex(0x6a2a3a);
    public static readonly Color LimeGround = Hex(0x1b2219);
    public static readonly Color LimeEdge = Hex(0x3d4f24);
    public static readonly Color Unaccounted = Hex(0x5a5070);

    private static readonly string UiFamily = FirstInstalled("Segoe UI Variable Text", "Segoe UI");
    private static readonly string MonoFamily = FirstInstalled("Cascadia Mono", "Consolas");

    public static readonly Font Body = new(UiFamily, 10f);
    public static readonly Font BodyStrong = new(UiFamily, 10f, FontStyle.Bold);
    public static readonly Font Caption = new(UiFamily, 7.5f, FontStyle.Bold);
    public static readonly Font Title = new(UiFamily, 15f, FontStyle.Bold);
    public static readonly Font Hero = new(UiFamily, 24f, FontStyle.Bold);
    public static readonly Font Figure = new(UiFamily, 13f);
    public static readonly Font Mono = new(MonoFamily, 9f);
    public static readonly Font MonoSmall = new(MonoFamily, 8f);
    public static readonly Font Small = new(UiFamily, 8.5f);
    public static readonly Font ForecastValue = new(UiFamily, 13f, FontStyle.Bold);

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    private static string FirstInstalled(params string[] names)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(installed.Contains) ?? FontFamily.GenericSansSerif.Name;
    }
}
