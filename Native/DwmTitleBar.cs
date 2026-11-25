using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

/// <summary>
/// The look of the system title bar and frame of a window (the DWM attributes of Windows 10 and 11). A call that the system does
/// not know (an older Windows) is ignored: the window keeps the system look.
/// </summary>
internal static class DwmTitleBar
{
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    /// <summary>The value that gives the colour back to the system.</summary>
    private const int ColorDefault = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    private static int Build => Environment.OSVersion.Version.Build;

    /// <summary>Whether the title bar can take colours of its own (Windows 11).</summary>
    public static bool SupportsColors => Environment.OSVersion.Version.Major >= 10 && Build >= 22000;

    /// <summary>Whether the title bar can be dark (Windows 10 version 1809 and later).</summary>
    public static bool SupportsDarkMode => Environment.OSVersion.Version.Major >= 10 && Build >= 17763;

    /// <summary>
    /// Gives the title bar the dark or the light frame and, where the system allows it, the colours of the theme (COLORREF values,
    /// 0x00BBGGRR). Pass <c>null</c> for the colours to give them back to the system (a high contrast theme).
    /// </summary>
    public static void Apply(IntPtr hwnd, bool dark, int? caption, int? text, int? border)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            if (SupportsDarkMode)
            {
                int on = dark ? 1 : 0;
                // The attribute had another number before Windows 10 version 2004.
                int attribute = Build >= 19041 ? UseImmersiveDarkMode : UseImmersiveDarkModeBefore20H1;
                DwmSetWindowAttribute(hwnd, attribute, ref on, sizeof(int));
            }
            if (SupportsColors)
            {
                SetColor(hwnd, CaptionColor, caption);
                SetColor(hwnd, TextColor, text);
                SetColor(hwnd, BorderColor, border);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>The bounds of a window in screen pixels; false when the window is gone.</summary>
    public static bool TryGetBounds(IntPtr hwnd, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect rect))
        {
            return false;
        }
        left = rect.Left;
        top = rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return true;
    }

    private static void SetColor(IntPtr hwnd, int attribute, int? color)
    {
        int value = color ?? ColorDefault;
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
    }
}
