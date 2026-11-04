using System.Runtime.InteropServices;

namespace DiskMon.Core.Native;

/// <summary>dwmapi calls the Tray needs: a dark title bar for the dashboard window.</summary>
public static partial class Dwmapi
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>Asks Windows 10 (20H1+) and 11 to draw <paramref name="hwnd"/>'s title bar dark. Returns false where unsupported.</summary>
    public static bool TryUseDarkTitleBar(nint hwnd)
    {
        var on = 1;
        return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) == 0;
    }

    /// <summary>The window's visible bounds on screen, without the invisible resize border that <c>GetWindowRect</c> includes.</summary>
    public static bool TryGetFrameBounds(nint hwnd, out System.Drawing.Rectangle bounds)
    {
        var ok = DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<Rect>()) == 0;
        bounds = ok ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : default;
        return ok && bounds.Width > 0 && bounds.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll", SetLastError = true)]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
}
