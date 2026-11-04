namespace DiskMon.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --screenshot <folder> opens only the dashboard, saves a PNG per range and size there, and exits. It skips the
        // single-instance check and the tray icon, so it runs beside the installed tray (used to review the UI).
        var shotAt = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (shotAt >= 0)
        {
            var folder = shotAt + 1 < args.Length ? args[shotAt + 1] : Path.Combine(Environment.CurrentDirectory, "screenshots");
            ApplicationConfiguration.Initialize();
            Application.Run(Dashboard.DashboardForm.ForScreenshots(TrayConfig.Load().Options, folder));
            return;
        }

        // One tray per logon session: the HKLM Run entry and a manual start must not show two icons.
        using var instance = new Mutex(initiallyOwned: true, @"Local\DiskMon.Tray", out var isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        // --dashboard opens the dashboard window right away (handy from a terminal or a shortcut).
        var openDashboard = args.Any(a => a.Equals("--dashboard", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayContext(TrayConfig.Load(), openDashboard));
    }
}
