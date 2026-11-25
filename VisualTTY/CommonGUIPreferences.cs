using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Xml.Serialization;

namespace VisualTTY;

public class CommonGUIPreferences
{
    public struct WindowPlacement
    {
        public ScreenRect LastKnownConnectionWindowRectangle;

        public ScreenRect[] ScreenRectanglesWhenWindowRectangleRemembered;

        public void Apply(Window wnd)
        {
            ScreenRect[] first = SmarTTY.Native.DisplayMonitors.AllBounds();
            if (ScreenRectanglesWhenWindowRectangleRemembered != null && first.SequenceEqual(ScreenRectanglesWhenWindowRectangleRemembered))
            {
                wnd.WindowStartupLocation = WindowStartupLocation.Manual;
                wnd.Top = LastKnownConnectionWindowRectangle.Top;
                wnd.Left = LastKnownConnectionWindowRectangle.Left;
                ((FrameworkElement)wnd).Width = LastKnownConnectionWindowRectangle.Width;
                ((FrameworkElement)wnd).Height = LastKnownConnectionWindowRectangle.Height;
            }
        }

        public void Save(Window wnd)
        {
            LastKnownConnectionWindowRectangle = new ScreenRect((int)wnd.Left, (int)wnd.Top, (int)((FrameworkElement)wnd).Width, (int)((FrameworkElement)wnd).Height);
            ScreenRectanglesWhenWindowRectangleRemembered = SmarTTY.Native.DisplayMonitors.AllBounds();
        }
    }

    public WindowPlacement MainWindow;

    public WindowPlacement ConnectionWindow;

    public ShownConnectionTypes ShownConnections = new ShownConnectionTypes
    {
        Serial = true,
        SSH = true,
        RawTcp = true
    };

    public ConnectionSortMode SortMode = ConnectionSortMode.LastUse;

    public bool ProbeConnections;

    /// <summary>
    /// The launcher checks every saved connection once when it opens and marks the ones that do not answer (on by default; an older
    /// file without it keeps the default).
    /// </summary>
    public bool MarkUnreachableConnections = true;

    [XmlIgnore]
    public DateTime _Timestamp;

    public bool FullScreen;

    public bool Minimalistic;

    /// <summary>
    /// The sizes of the windows (other than the main and the connection window, which have a placement of their own) that the user
    /// changed, by the name of the window class; see <see cref="WindowSizeList"/>.
    /// </summary>
    public List<RememberedWindowSize> WindowSizes = new List<RememberedWindowSize>();

    /// <summary>The width of the file manager beside the terminals in pixels; 0 when the user never changed it.</summary>
    public int FileListWidth;
}
