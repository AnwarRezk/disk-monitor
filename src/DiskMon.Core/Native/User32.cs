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
    private const long WsVScroll = 0x00200000;

    /// <summary>Whether <paramref name="hwnd"/> is showing its vertical scroll bar right now.</summary>
    public static bool HasVerticalScrollBar(nint hwnd) => (GetWindowLongPtrW(hwnd, GwlStyle) & WsVScroll) != 0;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
}
