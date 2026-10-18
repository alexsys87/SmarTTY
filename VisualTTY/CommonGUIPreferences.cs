using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace VisualTTY;

public class CommonGUIPreferences
{
    public struct WindowPlacement
    {
        public Rectangle LastKnownConnectionWindowRectangle;

        public Rectangle[] ScreenRectanglesWhenWindowRectangleRemembered;

        public void Apply(Window wnd)
        {
            Rectangle[] first = Screen.AllScreens.Select((Screen s) => s.Bounds).ToArray();
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
            LastKnownConnectionWindowRectangle = new Rectangle
            {
                X = (int)wnd.Left,
                Y = (int)wnd.Top,
                Width = (int)((FrameworkElement)wnd).Width,
                Height = (int)((FrameworkElement)wnd).Height
            };
            ScreenRectanglesWhenWindowRectangleRemembered = Screen.AllScreens.Select((Screen s) => s.Bounds).ToArray();
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
