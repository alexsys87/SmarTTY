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

    public static EdgeFlags GetNearEdges(Control control, Point point, int margin)
    {
        EdgeFlags edgeFlags = ScreenEdgeUtils.EdgeFlags.None;
        if (control == null)
        {
            return edgeFlags;
        }
        if (point.X < margin)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Left;
        }
        if (point.Y < margin)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Top;
        }
        if (point.X > control.Width - margin)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Right;
        }
        if (point.Y > control.Height - margin)
        {
            edgeFlags |= ScreenEdgeUtils.EdgeFlags.Bottom;
        }
        return edgeFlags;
    }
}
