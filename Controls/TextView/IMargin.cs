using SmarTTY.Controls;
using System.Drawing;

namespace SmarTTY.TextEditing;

public interface IMargin
{
    void Paint(Graphics graphics, Rectangle rectangle, SizeF charSize, int firstLineIndex, int lastLineIndex, int rowCount, ReadonlyTextBrowser readonlyTextBrowser);

    int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int lineCount);

    bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int firstLineIndex, int lastLineIndex, SizeF charSize, int rowCount);
}
