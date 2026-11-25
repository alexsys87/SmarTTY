using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

/// <summary>
/// The look of the system title bar and frame of a window (the DWM attributes of Windows 10 and 11) and the opacity of a whole
/// window. A call that the system does not know (an older Windows) is ignored: the window keeps the system look.
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

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int LwaAlpha = 0x2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwFrame = 0x0400;

    /// <summary>
    /// The message a window gets before its style changes; <c>wParam</c> is <see cref="GwlExStyle"/> for the extended style, <c>lParam</c>
    /// points to the old and the new style (a <c>STYLESTRUCT</c>), and the new one may be changed there.
    /// </summary>
    public const int WmStyleChanging = 0x007C;

    /// <summary>The <c>wParam</c> of <see cref="WmStyleChanging"/> when the extended style changes.</summary>
    public const int ExStyleIndex = GwlExStyle;

    // The offset of styleNew in STYLESTRUCT (two DWORDs: styleOld, styleNew).
    private const int StyleNewOffset = 4;

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

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

    /// <summary>Whether the window has the layered style (the one an opacity below 100 % needs).</summary>
    public static bool IsLayered(IntPtr hwnd)
    {
        try
        {
            return hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GwlExStyle) & WsExLayered) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps the layered style in a change of the extended style that would take it away (<see cref="WmStyleChanging"/> with
    /// <see cref="ExStyleIndex"/>; <paramref name="styleStruct"/> is its <c>lParam</c>). WPF writes the extended style it keeps for a
    /// window (which has no layered style) again when it sees fit; without this the opacity would be lost every time. Returns whether
    /// the new style was changed.
    /// </summary>
    public static bool KeepLayeredStyle(IntPtr styleStruct)
    {
        if (styleStruct == IntPtr.Zero)
        {
            return false;
        }
        int oldStyle = Marshal.ReadInt32(styleStruct);
        int newStyle = Marshal.ReadInt32(styleStruct, StyleNewOffset);
        if ((oldStyle & WsExLayered) == 0 || (newStyle & WsExLayered) != 0)
        {
            return false;
        }
        Marshal.WriteInt32(styleStruct, StyleNewOffset, newStyle | WsExLayered);
        return true;
    }

    /// <summary>
    /// Makes the whole window (the frame included) <paramref name="alpha"/> of 255 opaque; 255 takes the layered style off again,
    /// so a window that is not transparent is drawn as it always was. Returns whether the window is in the wanted state afterwards
    /// (read back from the system, not taken from the return value of the call).
    /// </summary>
    public static bool SetOpacity(IntPtr hwnd, byte alpha)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            int style = GetWindowLong(hwnd, GwlExStyle);
            bool layered = (style & WsExLayered) != 0;
            if (alpha == 255)
            {
                if (layered)
                {
                    SetWindowLong(hwnd, GwlExStyle, style & ~WsExLayered);
                    Refresh(hwnd);
                }
                return (GetWindowLong(hwnd, GwlExStyle) & WsExLayered) == 0;
            }
            if (layered && GetLayeredWindowAttributes(hwnd, out _, out byte current, out uint currentFlags)
                && (currentFlags & LwaAlpha) != 0 && current == alpha)
            {
                // Already as asked (the window is activated again, say): nothing to do, nothing to redraw.
                return true;
            }
            bool changed = false;
            if (!layered)
            {
                SetWindowLong(hwnd, GwlExStyle, style | WsExLayered);
                changed = true;
            }
            SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
            if (changed)
            {
                // A window that has shown content becomes a layered one only when its frame and its surface are drawn again.
                Refresh(hwnd);
            }
            return (GetWindowLong(hwnd, GwlExStyle) & WsExLayered) != 0
                && GetLayeredWindowAttributes(hwnd, out _, out byte actual, out uint flags)
                && (flags & LwaAlpha) != 0
                && actual == alpha;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Refresh(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwFrame | RdwAllChildren);
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
