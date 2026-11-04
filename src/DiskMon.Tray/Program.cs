namespace DiskMon.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray per logon session: the HKLM Run entry and a manual start must not show two icons.
        using var instance = new Mutex(initiallyOwned: true, @"Local\DiskMon.Tray", out var isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(TrayConfig.Load()));
    }
}
