using System.Windows;
using System.Windows.Media;

namespace SmarTTY.Utilities;

internal static class DrawingContextExtensions
{
    public static void DrawLine(this DrawingContext drawingContext, Pen pen, double value2, double value3, double value4, double value5)
    {
        drawingContext.DrawLine(pen, new Point(value2, value3), new Point(value4, value5));
    }
}
