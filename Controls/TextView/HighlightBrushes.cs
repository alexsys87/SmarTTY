using System.Collections.Generic;
using System.Drawing;
using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

/// <summary>
/// Makes the brushes and pens of <see cref="HighlightStyle"/> for painting. The model holds colours only: a brush or pen is
/// made once for a colour and kept, so painting does not allocate. The system colours of a selection are read at every call,
/// which is when the control paints.
/// </summary>
internal static class HighlightBrushes
{
    private static readonly Dictionary<RgbColor, SolidBrush> Brushes = new Dictionary<RgbColor, SolidBrush>();

    private static readonly Dictionary<RgbColor, Pen> Pens = new Dictionary<RgbColor, Pen>();

    /// <summary>The brush of the fill, or null when the style has no fill.</summary>
    public static Brush FillBrush(HighlightStyle style)
    {
        RgbColor color = style.FillColor;
        if (color.IsEmpty && style.UseSystemSelectionColors)
        {
            color = SystemColors.Highlight.ToRgb();
        }
        return BrushOf(color);
    }

    /// <summary>The brush of the text, or null when the text keeps its own colour.</summary>
    public static Brush TextBrush(HighlightStyle style)
    {
        RgbColor color = style.TextColor;
        if (color.IsEmpty && style.UseSystemSelectionColors)
        {
            color = SystemColors.HighlightText.ToRgb();
        }
        return BrushOf(color);
    }

    /// <summary>The 1-pixel pen of the border, or null when the style has no border.</summary>
    public static Pen BorderPen(HighlightStyle style)
    {
        RgbColor color = style.BorderColor;
        if (color.IsEmpty)
        {
            return null;
        }
        lock (Pens)
        {
            Pen pen;
            if (!Pens.TryGetValue(color, out pen))
            {
                pen = new Pen(color.ToColor(), 1f);
                Pens[color] = pen;
            }
            return pen;
        }
    }

    private static Brush BrushOf(RgbColor color)
    {
        if (color.IsEmpty)
        {
            return null;
        }
        lock (Brushes)
        {
            SolidBrush brush;
            if (!Brushes.TryGetValue(color, out brush))
            {
                brush = new SolidBrush(color.ToColor());
                Brushes[color] = brush;
            }
            return brush;
        }
    }
}
