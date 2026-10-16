using System.Drawing;

namespace SmarTTY.TextEditing;

public class HighlightStyle
{
    public Brush FillBrush = new SolidBrush(SystemColors.Highlight);

    public Pen BorderPen;

    public Brush TextBrush = new SolidBrush(SystemColors.HighlightText);

    public HighlightStyle(Brush brush, Pen pen, Brush brush2)
    {
        this.FillBrush = brush;
        this.BorderPen = pen;
        this.TextBrush = brush2;
    }

    public HighlightStyle()
    {
    }
}
