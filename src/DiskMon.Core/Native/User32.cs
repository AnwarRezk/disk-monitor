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

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
