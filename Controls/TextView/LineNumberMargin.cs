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

    public void Paint(Graphics graphics, Rectangle rectangle, SizeF charSize, int firstLineIndex, int lastLineIndex, int rowCount, ReadonlyTextBrowser readonlyTextBrowser)
    {
        Brush brush = this._browser.Brush;
        graphics.FillRectangle(brush, rectangle);
        float yPos = rectangle.Top;
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            int lineIndex = readonlyTextBrowser.DisplayRows[rowIdx].Line;
            if (readonlyTextBrowser.DisplayRows[rowIdx].VirtualLine == null && lineIndex != -1 && !readonlyTextBrowser.DisplayRows[rowIdx].Continuation)
            {
                graphics.DrawString((lineIndex + 1).ToString(), readonlyTextBrowser.Font, this._browser.LineNumberForegroundBrush, rectangle.Right - 5, yPos, this._format);
            }
            yPos += charSize.Height;
        }
    }

    public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int lineCount)
    {
        if (lineCount < 99999)
        {
            lineCount = 99999;
        }
        return (int)Math.Ceiling(graphics.MeasureString(lineCount.ToString(), readonlyTextBrowser.Font, readonlyTextBrowser.Width, this._format).Width) + 5;
    }

    public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int firstLineIndex, int lastLineIndex, SizeF charSize, int rowCount)
    {
        return false;
    }
}
