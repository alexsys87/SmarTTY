using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmarTTY.Native;

/// <summary>
/// Tells when Windows announces a new COM port (a USB-serial adapter plugged in again). The announcement,
/// WM_DEVICECHANGE with DBT_DEVICEARRIVAL for a port, is broadcast to top-level windows only, so a hidden top-level
/// window listens for it. Created on first use on the UI thread; the event is raised on that thread.
/// </summary>
public static class SerialPortArrivalWatcher
{
    private const int WmDeviceChange = 0x0219;

    private const int DbtDeviceArrival = 0x8000;

    private const int DbtDevTypPort = 3;

    // DEV_BROADCAST_PORT: dbcp_size, dbcp_devicetype, dbcp_reserved, then the port name.
    private const int PortNameOffset = 12;

    private sealed class ListenerWindow : NativeWindow
    {
        public ListenerWindow()
        {
            // A plain top-level window that is never shown; a message-only window would not get the broadcast.
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange && m.WParam.ToInt64() == DbtDeviceArrival && m.LParam != IntPtr.Zero)
            {
                string portName = TryReadPortName(m.LParam);
                if (portName != null)
                {
                    try
                    {
                        PortArrived?.Invoke(portName);
                    }
                    catch
                    {
                        // A failing subscriber must not break the message loop.
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    private static ListenerWindow _window;

    private static event Action<string> PortArrived;

    /// <summary>Starts listening (once) and adds a handler that gets the name of each port that appears, such as "COM3".</summary>
    public static void Subscribe(Action<string> handler)
    {
        if (_window == null)
        {
            try
            {
                _window = new ListenerWindow();
            }
            catch (Exception)
            {
                // Without the window nothing is announced; reconnecting then falls back to its timer.
                return;
            }
        }
        PortArrived += handler;
    }

    public static void Unsubscribe(Action<string> handler)
    {
        PortArrived -= handler;
    }

    private static string TryReadPortName(IntPtr header)
    {
        try
        {
            int size = Marshal.ReadInt32(header, 0);
            int deviceType = Marshal.ReadInt32(header, 4);
            if (deviceType != DbtDevTypPort || size <= PortNameOffset)
            {
                return null;
            }
            // NativeWindow is a Unicode window, so the name arrives as UTF-16.
            string name = Marshal.PtrToStringUni(IntPtr.Add(header, PortNameOffset), (size - PortNameOffset) / 2);
            int end = name.IndexOf('\0');
            if (end >= 0)
            {
                name = name.Substring(0, end);
            }
            return name.Length > 0 ? name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
