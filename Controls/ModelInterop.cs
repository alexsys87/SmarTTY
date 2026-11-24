using System.Drawing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

/// <summary>
/// The only place where the types of the logic folders (<see cref="RgbColor"/>, <see cref="GridPoint"/>) meet the GDI+ types of the
/// same meaning. The conversions of <see cref="VirtualKey"/> to and from the WinForms keys went to <c>tests/ModelInteropTests</c> in stage
/// 5B, which still checks the two against each other; nothing of the application uses the WinForms keys.
/// </summary>
public static class ModelInterop
{
    /// <summary>
    /// Converts a colour to its components. A named or system colour is resolved to its value, so the result of two
    /// colours that look the same is equal even when <c>==</c> on the GDI+ colours says otherwise.
    /// </summary>
    public static RgbColor ToRgb(this Color color)
    {
        return RgbColor.FromArgb(color.ToArgb());
    }

    /// <summary>
    /// Converts a colour to the GDI+ colour. <see cref="RgbColor.Empty"/> becomes <see cref="Color.Empty"/>, so the
    /// "not set" colour survives a round trip; any other colour becomes the colour built from its components.
    /// </summary>
    public static Color ToColor(this RgbColor color)
    {
        return color.IsEmpty ? Color.Empty : Color.FromArgb(color.ToArgb());
    }

    /// <summary>Converts a character grid position (X = column, Y = row) to a point with the same numbers.</summary>
    public static Point ToPoint(this GridPoint point)
    {
        return new Point(point.X, point.Y);
    }

    /// <summary>Converts a point with a column in X and a row in Y to a grid position.</summary>
    public static GridPoint ToGridPoint(this Point point)
    {
        return new GridPoint(point.X, point.Y);
    }
}
