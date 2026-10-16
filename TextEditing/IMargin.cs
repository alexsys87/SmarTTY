using SmarTTY.Controls;
using System.Drawing;

namespace SmarTTY.TextEditing;

public interface IMargin
{
    void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int value2, int value3, int value4, ReadonlyTextBrowser readonlyTextBrowser);

    int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int value2);

    bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, SizeF sizeF, int value4);
}
