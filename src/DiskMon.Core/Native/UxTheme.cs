using System.Runtime.InteropServices;

namespace DiskMon.Core.Native;

/// <summary>uxtheme calls the Tray needs: dark scroll bars and headers on the dashboard's lists.</summary>
public static partial class UxTheme
{
    /// <summary>Applies Explorer's dark theme to <paramref name="hwnd"/>. Returns false where Windows has no dark theme.</summary>
    public static bool TryUseDarkTheme(nint hwnd) => SetWindowTheme(hwnd, "DarkMode_Explorer", null) == 0;

    [LibraryImport("uxtheme.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);
}
