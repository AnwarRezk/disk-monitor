using DiskMon.Core.Native;

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

        // --dashboard opens the dashboard window right away (the Start menu and desktop shortcuts pass it).
        var openDashboard = args.Any(a => a.Equals("--dashboard", StringComparison.OrdinalIgnoreCase));

        // Created before the mutex, so it exists whenever a second launch sees the mutex taken.
        using var dashboardRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DiskMon.Tray.OpenDashboard");

        // One tray per logon session: the HKLM Run entry and a manual start must not show two icons.
        using var instance = new Mutex(initiallyOwned: true, @"Local\DiskMon.Tray", out var isFirst);
        if (!isFirst)
        {
            // The Tray is already running: a shortcut asks it to show its dashboard instead.
            if (openDashboard)
            {
                User32.AllowAnySetForegroundWindow();
                dashboardRequest.Set();
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(TrayConfig.Load(), dashboardRequest, openDashboard));
    }
}
