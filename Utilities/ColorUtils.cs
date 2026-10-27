using System;
using System.Globalization;

namespace SmarTTY.Utilities;

/// <summary>
/// Colours as text and the fixed palette of the colour picker. The application stores a colour as three channels, so
/// the picker shows it as "#RRGGBB" and reads that form back. Both directions live here, away from the window, so they
/// can be checked without a display.
/// </summary>
public static class ColorUtils
{
    /// <summary>
    /// The 48 colours of the picker's basic grid, eight per row. It keeps the well known system colours (the greys,
    /// the pure primaries and secondaries, the Windows 3.1 pastels) so a user recognises them, but holds no duplicate.
    /// </summary>
    public static readonly string[] BasicColors = new string[48]
    {
        "#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF", "#800000", "#FF0000", "#FF8000",
        "#FFFF00", "#80FF00", "#00FF00", "#00FF80", "#00FFFF", "#0080FF", "#0000FF", "#8000FF",
        "#FF00FF", "#FF0080", "#FFC0C0", "#FFE0C0", "#FFFFC0", "#C0FFC0", "#C0FFFF", "#C0E0FF",
        "#C0C0FF", "#E0C0FF", "#FFC0FF", "#FFC0E0", "#808000", "#008000", "#008080", "#000080",
        "#800080", "#A0A0A4", "#A6CAF0", "#C0DCC0", "#FFFBF0", "#FFD0A0", "#D0FFA0", "#A0FFD0",
        "#A0D0FF", "#D0A0FF", "#FFA0D0", "#FF4000", "#40FF00", "#00FF40", "#0040FF", "#4000FF"
    };

    /// <summary>The colour as "#RRGGBB"; channels outside 0-255 are clamped instead of wrapping.</summary>
    public static string FormatHex(int red, int green, int blue)
    {
        return "#" + Channel(red) + Channel(green) + Channel(blue);
    }

    /// <summary>
    /// Reads "#RRGGBB", "RRGGBB" or the three-digit shorthand "#RGB". Returns false for anything else, so the caller
    /// can ignore a half-typed value instead of guessing a colour from it.
    /// </summary>
    public static bool TryParseHex(string text, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;
        if (text == null)
        {
            return false;
        }
        string value = text.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value.Substring(1);
        }
        if (value.Length == 3 && IsHexDigits(value))
        {
            // "#abc" is "#aabbcc": each digit is doubled.
            value = new string(new char[6] { value[0], value[0], value[1], value[1], value[2], value[2] });
        }
        if (value.Length != 6 || !IsHexDigits(value))
        {
            return false;
        }
        red = ParseChannel(value, 0);
        green = ParseChannel(value, 2);
        blue = ParseChannel(value, 4);
        return true;
    }

    /// <summary>Limits one channel to the byte range the settings store.</summary>
    public static int Clamp(int channel)
    {
        if (channel < 0)
        {
            return 0;
        }
        return (channel > 255) ? 255 : channel;
    }

    private static string Channel(int value)
    {
        return Clamp(value).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static int ParseChannel(string value, int index)
    {
        return int.Parse(value.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static bool IsHexDigits(string value)
    {
        foreach (char character in value)
        {
            bool digit = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F');
            if (!digit)
            {
                return false;
            }
        }
        return value.Length > 0;
    }
}
