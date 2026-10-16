using SmarTTY.Controls;
using System;
using System.Drawing;

namespace SmarTTY.TextEditing;

internal class LineNumberMargin : IMargin
{
    private StringFormat _format = new StringFormat();

    private ReadonlyTextBrowser _browser;

    public LineNumberMargin(ReadonlyTextBrowser readonlyTextBrowser)
    {
        _browser = readonlyTextBrowser;
        _format.Alignment = StringAlignment.Far;
    }

    public void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int value2, int value3, int value4, ReadonlyTextBrowser readonlyTextBrowser)
    {
        Brush brush = this._browser.Brush;
        graphics.FillRectangle(brush, rectangle);
        float num = rectangle.Top;
        for (int num2 = 0; num2 < value4; num2++)
        {
            int num3 = readonlyTextBrowser.DisplayRows[num2].Line;
            if (readonlyTextBrowser.DisplayRows[num2].VirtualLine == null && num3 != -1)
            {
                graphics.DrawString((num3 + 1).ToString(), readonlyTextBrowser.Font, this._browser.Brush2, rectangle.Right - 5, num, this._format);
            }
            num += sizeF.Height;
        }
    }

    public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int value2)
    {
        if (value2 < 99999)
        {
            value2 = 99999;
        }
        return (int)Math.Ceiling(graphics.MeasureString(value2.ToString(), readonlyTextBrowser.Font, readonlyTextBrowser.Width, this._format).Width) + 5;
    }

    public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, SizeF sizeF, int value4)
    {
        return false;
    }
}
