using System;

namespace SmarTTY.Utilities;

/// <summary>
/// The rules of the window chrome that need no window (UI-free, so the tests run them on plain .NET): the opacity of the windows and
/// the colours the title bar is given, and the size a remembered window gets back.
/// </summary>
public static class WindowChromeRules
{
    /// <summary>The least opacity the settings allow: a window at 0 % could not be found again.</summary>
    public const int MinOpacityPercent = 30;

    /// <summary>The opacity of a window that is not transparent at all.</summary>
    public const int MaxOpacityPercent = 100;

    /// <summary>The opacity in per cent brought into the range the settings allow.</summary>
    public static int ClampOpacityPercent(int percent)
    {
        if (percent < MinOpacityPercent)
        {
            return MinOpacityPercent;
        }
        return percent > MaxOpacityPercent ? MaxOpacityPercent : percent;
    }

    /// <summary>The alpha value (0..255) of a window with this opacity; 100 % is 255.</summary>
    public static byte OpacityToAlpha(int percent)
    {
        return (byte)Math.Round(ClampOpacityPercent(percent) * 255.0 / 100.0);
    }

    /// <summary>A colour as the COLORREF the DWM takes (0x00BBGGRR).</summary>
    public static int ToColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    /// <summary>Whether a background of this colour is a dark one (the title bar then gets the dark frame).</summary>
    public static bool IsDark(byte red, byte green, byte blue)
    {
        // The perceived brightness (ITU-R BT.601 weights), the same split the theme list uses for light and dark.
        double brightness = (0.299 * red + 0.587 * green + 0.114 * blue) / 255.0;
        return brightness < 0.5;
    }

    /// <summary>
    /// A remembered length of a window (its width or height) brought into what the window and the screen allow, or NaN when
    /// nothing is to be applied (a missing or nonsense value).
    /// </summary>
    /// <param name="saved">The remembered length.</param>
    /// <param name="min">The least the window allows (<c>MinWidth</c>); a value past <paramref name="available"/> still wins.</param>
    /// <param name="max">The most the window allows (<c>MaxWidth</c>, infinity when unlimited).</param>
    /// <param name="available">The length of the monitor the window is on; 0 or less when unknown.</param>
    public static double ClampLength(double saved, double min, double max, double available)
    {
        if (double.IsNaN(saved) || double.IsInfinity(saved) || saved <= 0)
        {
            return double.NaN;
        }
        double limit = double.IsNaN(max) || max <= 0 ? double.PositiveInfinity : max;
        if (available > 0 && available < limit)
        {
            limit = available;
        }
        double length = Math.Min(saved, limit);
        if (!double.IsNaN(min) && min > length)
        {
            length = min;
        }
        return length;
    }

    /// <summary>
    /// Where a window of <paramref name="size"/> starts (one axis, pixels) to keep the middle it had, brought into
    /// <paramref name="boundsStart"/>..<paramref name="boundsEnd"/> when it fits there.
    /// </summary>
    public static double CenteredStart(double center, double size, double boundsStart, double boundsEnd)
    {
        double start = center - size / 2.0;
        double last = boundsEnd - size;
        if (last < boundsStart)
        {
            // Wider than the monitor: start at its edge.
            return boundsStart;
        }
        if (start > last)
        {
            start = last;
        }
        return start < boundsStart ? boundsStart : start;
    }
}
