using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

/// <summary>
/// What a window drawn with per-pixel alpha (a WPF window with <c>AllowsTransparency</c>) needs of the system: the blur behind its
/// transparent parts, and a maximized size that is the work area of its monitor (a window without a system frame would otherwise cover
/// the task bar and reach past the edges). A call that the system does not know (an older Windows) is ignored.
/// </summary>
/// <remarks>
/// The blur is the accent policy of the window (<c>SetWindowCompositionAttribute</c>, <c>ACCENT_ENABLE_BLURBEHIND</c>), which Windows 10
/// (1803 and later) and 11 apply behind a layered window. A plain WPF window with the system frame is no layered window: the DWM ignores
/// the alpha of its pixels on Windows 10 and shows black where it is transparent (seen on Windows 10 22H2, with the frame extended over the
/// client area and with the accent policy alike), so the see-through window is made with <c>AllowsTransparency</c> instead.
/// </remarks>
internal static class DwmBackdrop
{
    private const int AccentPolicyAttribute = 19;
    private const int AccentDisabled = 0;
    private const int AccentBlurBehind = 3;

    /// <summary>The message that asks a window for its maximized size and place.</summary>
    public const int WmGetMinMaxInfo = 0x0024;

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
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
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Blurs what is behind the transparent parts of the window, or stops blurring it.</summary>
    public static void SetBlur(IntPtr hwnd, bool blur)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            AccentPolicy accent = new AccentPolicy { AccentState = blur ? AccentBlurBehind : AccentDisabled };
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, data, false);
                CompositionAttributeData attribute = new CompositionAttributeData { Attribute = AccentPolicyAttribute, Data = data, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref attribute);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Answers <see cref="WmGetMinMaxInfo"/> for a window without a system frame: maximized, it takes the work area of its monitor (the
    /// task bar stays visible). Returns false when the monitor could not be asked (the message is then left to the window).
    /// </summary>
    public static bool FitMaximizedToWorkArea(IntPtr hwnd, IntPtr minMaxInfo)
    {
        if (hwnd == IntPtr.Zero || minMaxInfo == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return false;
            }
            MinMaxInfo value = (MinMaxInfo)Marshal.PtrToStructure(minMaxInfo, typeof(MinMaxInfo));
            value.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            value.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            value.MaxSize.X = info.Work.Right - info.Work.Left;
            value.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            Marshal.StructureToPtr(value, minMaxInfo, false);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
