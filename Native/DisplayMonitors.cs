using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VisualTTY;

namespace SmarTTY.Native;

/// <summary>
/// The monitors of the desktop and their bounds, as the WinForms <c>Screen</c> gave them (stage 5B): the same Win32 calls
/// (<c>EnumDisplayMonitors</c>, <c>MonitorFromPoint</c> with the nearest monitor, <c>GetMonitorInfo</c>), the same order and the
/// whole area of a monitor in its pixels.
/// </summary>
internal static class DisplayMonitors
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>The bounds of every monitor, in the order Windows lists them (that of <c>Screen.AllScreens</c>).</summary>
    public static ScreenRect[] AllBounds()
    {
        List<ScreenRect> bounds = new List<ScreenRect>();
        MonitorEnumProc callback = (monitor, hdc, rect, data) =>
        {
            if (TryGetBounds(monitor, out ScreenRect rectangle))
            {
                bounds.Add(rectangle);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return bounds.ToArray();
    }

    /// <summary>The bounds of the monitor a point is on, or of the nearest one (<c>Screen.FromPoint(...).Bounds</c>).</summary>
    public static ScreenRect BoundsAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        return TryGetBounds(monitor, out ScreenRect rectangle) ? rectangle : ScreenRect.Empty;
    }

    private static bool TryGetBounds(IntPtr monitor, out ScreenRect bounds)
    {
        MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            bounds = ScreenRect.Empty;
            return false;
        }
        bounds = ScreenRect.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        return true;
    }
}
