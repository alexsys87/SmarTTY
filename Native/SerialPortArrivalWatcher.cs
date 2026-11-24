using System;
using System.Runtime.InteropServices;

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

    // A plain top-level window of a class of its own that is never shown (a message-only window would not get the broadcast). It was a
    // WinForms NativeWindow until stage 5B; the WPF dispatcher of the UI thread pumps its messages as it did those of the NativeWindow.
    private sealed class ListenerWindow
    {
        private const string ClassName = "SmarTTY.SerialPortArrivalWatcher";

        private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public WindowProc Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string MenuName;
            public string Name;
            public IntPtr SmallIcon;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        // Kept alive for the life of the window: Windows calls it.
        private readonly WindowProc _procedure;

        private readonly IntPtr _handle;

        public ListenerWindow()
        {
            _procedure = Procedure;
            WindowClass windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf(typeof(WindowClass)),
                Procedure = _procedure,
                Instance = GetModuleHandle(null),
                Name = ClassName
            };
            if (RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException("The window class of the COM port watcher was not registered (" + Marshal.GetLastWin32Error() + ").");
            }
            _handle = CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The window of the COM port watcher was not made (" + Marshal.GetLastWin32Error() + ").");
            }
        }

        private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WmDeviceChange && wParam.ToInt64() == DbtDeviceArrival && lParam != IntPtr.Zero)
            {
                string portName = TryReadPortName(lParam);
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
            return DefWindowProc(window, message, wParam, lParam);
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
            // The window class is a Unicode one, so the name arrives as UTF-16.
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
