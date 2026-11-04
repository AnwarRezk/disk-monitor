namespace DiskMon.Tray;

/// <summary>
/// Hidden application context owning the tray icon. The rendered free-space icon, the dashboard
/// and the pipe status check arrive in M2; for now it only proves the WinForms host runs.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DiskMon",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
