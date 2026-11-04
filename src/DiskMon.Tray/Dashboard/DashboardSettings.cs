using System.Text.Json;
using DiskMon.Core.Analysis;

namespace DiskMon.Tray.Dashboard;

/// <summary>
/// What the dashboard reopens with: Volume, range, tab and window placement. Kept per user in
/// <c>%LOCALAPPDATA%\DiskMon\dashboard.json</c>; the Tray never writes the shared database.
/// </summary>
internal sealed record DashboardSettings(
    string? Volume = null,
    DashboardRange Range = DashboardRange.Hours24,
    string Tab = "space",
    int X = 0,
    int Y = 0,
    int Width = 0,
    int Height = 0,
    bool Maximized = false)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMon", "dashboard.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The saved settings, or the defaults when there are none or the file is unreadable.</summary>
    public static DashboardSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<DashboardSettings>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Saves quietly: losing a window position is not worth an error dialog.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The saved window bounds when they are sane and still on a connected screen.</summary>
    public Rectangle? Bounds(Size minimum)
    {
        if (Width < minimum.Width || Height < minimum.Height)
            return null;
        var rect = new Rectangle(X, Y, Width, Height);
        // At least a title bar's worth must be visible somewhere, or the window would open off-screen.
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect with { Height = 40 })) ? rect : null;
    }
}
