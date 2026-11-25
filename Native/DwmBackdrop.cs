using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

/// <summary>
/// Lets what is behind a window show through the parts of its client area that the window draws transparent (with the alpha of its
/// pixels), blurred or clear. The window must draw its client area with alpha itself (for WPF: a transparent background of the
/// window and of its composition target). A call that the system does not know (an older Windows) is ignored.
/// </summary>
/// <remarks>
/// The client area is made "glass" with <c>DwmExtendFrameIntoClientArea</c>, so that the DWM uses the alpha of the window's pixels.
/// Behind it: on Windows 11 22H2 and later with blur, the acrylic system backdrop (<c>DWMWA_SYSTEMBACKDROP_TYPE</c>, the documented way);
/// otherwise the accent policy of the window (<c>SetWindowCompositionAttribute</c>, what Windows 10 has: a blur, or a clear gradient
/// with no colour).
/// </remarks>
internal static class DwmBackdrop
{
    private const int SystemBackdropType = 38;
    private const int BackdropAuto = 0;
    private const int BackdropNone = 1;
    private const int BackdropAcrylic = 3;

    private const int AccentPolicyAttribute = 19;
    private const int AccentDisabled = 0;
    private const int AccentTransparentGradient = 2;
    private const int AccentBlurBehind = 3;

    /// <summary>The gradient colour of the accent is used (with its alpha, here 0: no tint at all).</summary>
    private const int AccentFlagUseGradientColor = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    private static int Build => Environment.OSVersion.Version.Build;

    /// <summary>Whether the acrylic system backdrop is there (Windows 11 version 22H2 and later).</summary>
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Major >= 10 && Build >= 22621;

    /// <summary>Makes the transparent parts of the client area show what is behind the window, blurred or clear.</summary>
    public static void Enable(IntPtr hwnd, bool blur)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            ExtendFrame(hwnd, -1);
            if (SupportsSystemBackdrop && blur)
            {
                SetAccent(hwnd, AccentDisabled);
                SetBackdrop(hwnd, BackdropAcrylic);
                return;
            }
            if (SupportsSystemBackdrop)
            {
                SetBackdrop(hwnd, BackdropNone);
            }
            SetAccent(hwnd, blur ? AccentBlurBehind : AccentTransparentGradient);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Gives the window its usual opaque look back.</summary>
    public static void Disable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            SetAccent(hwnd, AccentDisabled);
            if (SupportsSystemBackdrop)
            {
                SetBackdrop(hwnd, BackdropAuto);
            }
            ExtendFrame(hwnd, 0);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static void ExtendFrame(IntPtr hwnd, int margin)
    {
        Margins margins = new Margins { Left = margin, Right = margin, Top = margin, Bottom = margin };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static void SetBackdrop(IntPtr hwnd, int type)
    {
        DwmSetWindowAttribute(hwnd, SystemBackdropType, ref type, sizeof(int));
    }

    private static void SetAccent(IntPtr hwnd, int state)
    {
        AccentPolicy accent = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = state == AccentTransparentGradient ? AccentFlagUseGradientColor : 0,
            GradientColor = 0
        };
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
}
