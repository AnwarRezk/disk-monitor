namespace DiskMon.Tray.Dashboard;

/// <summary>
/// The dashboard's "Violet Pro" look (option B of the 2026-11-04 redesign): a deep violet ground under a header band,
/// cards with hairline borders, the logo's violet→pink ramp for the gauge and bars, a pink free-space line, lime for
/// good news and a filled violet primary button.
/// </summary>
internal static class Theme
{
    public static readonly Color Ground = Hex(0x130e1d);
    public static readonly Color Band = Hex(0x160f22);
    public static readonly Color Card = Hex(0x1a1426);
    public static readonly Color Raised = Hex(0x221a33);
    public static readonly Color Drawer = Hex(0x1b1429);
    public static readonly Color PlotGround = Hex(0x120c1c);
    public static readonly Color Hairline = Hex(0x2a2140);
    public static readonly Color StrongEdge = Hex(0x3a2f5a);
    public static readonly Color Grid = Hex(0x241c36);
    public static readonly Color Text = Hex(0xf5f2fb);
    public static readonly Color Muted = Hex(0xb5adc8);
    public static readonly Color Selected = Hex(0x3a2b6a);
    public static readonly Color SelectedEdge = Hex(0x7c6cf0);
    public static readonly Color Accent = Hex(0x7c6cf0);
    public static readonly Color AccentHover = Hex(0x8d7ef5);
    public static readonly Color Line = Hex(0xfa7faa);
    public static readonly Color Fill = Hex(0x6a5fc1);
    public static readonly Color Lime = Hex(0xc2ef4e);
    public static readonly Color Growth = Hex(0xff8fb4);
    public static readonly Color Release = Hex(0xc2ef4e);
    public static readonly Color Hover = Hex(0x2a2045);
    public static readonly Color RowHover = Hex(0x1f1830);
    public static readonly Color Faint = Hex(0x8e86a3);
    public static readonly Color Amber = Hex(0xffc04d);
    public static readonly Color AmberGround = Hex(0x33270f);
    public static readonly Color AmberEdge = Hex(0x5a4420);
    public static readonly Color Red = Hex(0xff6b8a);
    public static readonly Color RedGround = Hex(0x2e1424);
    public static readonly Color RedEdge = Hex(0x6a2a3a);
    public static readonly Color LimeGround = Hex(0x1b2219);
    public static readonly Color LimeEdge = Hex(0x3d4f24);
    public static readonly Color Unaccounted = Hex(0x6c6380);

    /// <summary>The logo's arc ramp: violet rising to pink (gauge, Top Writers bars).</summary>
    public static readonly Color RampFrom = Hex(0x6a5fc1);
    public static readonly Color RampTo = Hex(0xfa7faa);

    private static readonly string UiFamily = FirstInstalled("Segoe UI Variable Text", "Segoe UI");
    private static readonly string MonoFamily = FirstInstalled("Cascadia Mono", "Consolas");

    public static readonly Font Body = new(UiFamily, 10f);
    public static readonly Font BodyStrong = new(UiFamily, 10f, FontStyle.Bold);
    public static readonly Font Caption = new(UiFamily, 7.5f, FontStyle.Bold);
    public static readonly Font Title = new(UiFamily, 13f, FontStyle.Bold);
    public static readonly Font Hero = new(UiFamily, 27f, FontStyle.Bold);
    public static readonly Font Figure = new(UiFamily, 13f);
    public static readonly Font Mono = new(MonoFamily, 9f);
    public static readonly Font MonoSmall = new(MonoFamily, 8f);
    public static readonly Font Small = new(UiFamily, 8.5f);
    public static readonly Font ForecastValue = new(UiFamily, 13f, FontStyle.Bold);
    public static readonly Font TileValue = new(UiFamily, 16f, FontStyle.Bold);
    public static readonly Font Section = new(UiFamily, 11f, FontStyle.Bold);
    public static readonly Font Tab = new(UiFamily, 10f, FontStyle.Bold);

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    private static string FirstInstalled(params string[] names)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(installed.Contains) ?? FontFamily.GenericSansSerif.Name;
    }
}
