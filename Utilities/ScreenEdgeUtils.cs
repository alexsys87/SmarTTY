using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Utilities;

public static class ScreenEdgeUtils
{
    public enum EdgeFlags
    {
        None = 0,
        Top = 1,
        Left = 2,
        Right = 4,
        Bottom = 8
    }

    public static EdgeFlags GetNearEdges(Control control, Point point, int value2)
    {
        EdgeFlags edgeFlags = ScreenEdgeUtils.EdgeFlags.None;
        if (control == null)
        {
            return edgeFlags;
        }
        if (point.X < value2)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Left;
        }
        if (point.Y < value2)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Top;
        }
        if (point.X > control.Width - value2)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Right;
        }
        if (point.Y > control.Height - value2)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Bottom;
        }
        return edgeFlags;
    }
}
