using System.Runtime.InteropServices;

namespace DiskMon.Core.Native;

/// <summary>user32 calls the Tray needs. Public because the Tray assembly uses them, and every P/Invoke lives in Core.</summary>
public static partial class User32
{
    /// <summary>Frees an icon handle such as the one <c>Bitmap.GetHicon()</c> returns; <c>Icon.Dispose()</c> does not.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    private const int GwlStyle = -16;
    private const long WsHScroll = 0x00100000;

    /// <summary>
    /// Clears <paramref name="hwnd"/>'s horizontal scroll bar style. A list whose columns are always sized to its width
    /// calls this while it computes its non-client area, so a bar never shows for a pixel of rounding.
    /// </summary>
    public static void HideHorizontalScrollBar(nint hwnd)
    {
        var style = GetWindowLongPtrW(hwnd, GwlStyle);
        if ((style & WsHScroll) != 0)
            SetWindowLongPtrW(hwnd, GwlStyle, style & ~(nint)WsHScroll);
    }

    private const int AsfwAny = -1;

    /// <summary>
    /// Lets any process bring a window to the foreground once. A second Tray launch (the Start menu shortcut) calls it
    /// before asking the running Tray to open its dashboard, which Windows' foreground lock would otherwise keep behind.
    /// </summary>
    public static bool AllowAnySetForegroundWindow() => AllowSetForegroundWindow(AsfwAny);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
