using System;
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

    [XmlIgnore]
    public DateTime _Timestamp;

    public bool FullScreen;

    public bool Minimalistic;
}
