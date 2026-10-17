using System.Drawing;

namespace SmarTTY.TextEditing;

public class HighlightStyle
{
    public Brush FillBrush = new SolidBrush(SystemColors.Highlight);

    public Pen BorderPen;

    public Brush TextBrush = new SolidBrush(SystemColors.HighlightText);

    public HighlightStyle(Brush fillBrush, Pen pen, Brush textBrush)
    {
        this.FillBrush = fillBrush;
        this.BorderPen = pen;
        this.TextBrush = textBrush;
    }

    public HighlightStyle()
    {
    }
}
