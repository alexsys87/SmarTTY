using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

/// <summary>
/// Puts the composition window and the candidate list of the input method (IME) at the caret of a view that draws its own text. WPF does
/// that only for its text boxes; for any other element the IME opens its window at the corner of the window. The calls are those of the
/// IMM32 API, which the IMEs of the Text Services Framework honour too. Positions are in device pixels of the client area of the window.
/// </summary>
public static class ImeCompositionWindow
{
    private const int CFS_POINT = 0x0002;

    private const int CFS_FORCE_POSITION = 0x0020;

    private const int CFS_EXCLUDE = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPOSITIONFORM
    {
        public int dwStyle;

        public POINT ptCurrentPos;

        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM
    {
        public int dwIndex;

        public int dwStyle;

        public POINT ptCurrentPos;

        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight;

        public int lfWidth;

        public int lfEscapement;

        public int lfOrientation;

        public int lfWeight;

        public byte lfItalic;

        public byte lfUnderline;

        public byte lfStrikeOut;

        public byte lfCharSet;

        public byte lfOutPrecision;

        public byte lfClipPrecision;

        public byte lfQuality;

        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM form);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetCandidateWindow(IntPtr hIMC, ref CANDIDATEFORM form);

    [DllImport("imm32.dll", EntryPoint = "ImmSetCompositionFontW", CharSet = CharSet.Unicode)]
    private static extern bool ImmSetCompositionFont(IntPtr hIMC, ref LOGFONT font);

    /// <summary>
    /// Moves the composition window to the cell of the caret (<paramref name="x"/>, <paramref name="y"/>: its top left corner;
    /// <paramref name="cellHeight"/>: its height) and keeps the candidate list below that row. False when the window has no input context
    /// (no IME is active) or the calls failed; that is harmless.
    /// </summary>
    public static bool MoveTo(IntPtr hwnd, int x, int y, int cellHeight, string fontName)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        IntPtr context = IntPtr.Zero;
        try
        {
            context = ImmGetContext(hwnd);
            if (context == IntPtr.Zero)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(fontName))
            {
                LOGFONT font = new LOGFONT
                {
                    lfHeight = -Math.Max(cellHeight, 1),
                    lfWeight = 400,
                    lfCharSet = 1,
                    lfFaceName = fontName.Length > 31 ? fontName.Substring(0, 31) : fontName
                };
                ImmSetCompositionFont(context, ref font);
            }
            COMPOSITIONFORM composition = new COMPOSITIONFORM
            {
                dwStyle = CFS_POINT | CFS_FORCE_POSITION,
                ptCurrentPos = new POINT { X = x, Y = y }
            };
            bool moved = ImmSetCompositionWindow(context, ref composition);
            CANDIDATEFORM candidates = new CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = CFS_EXCLUDE,
                ptCurrentPos = new POINT { X = x, Y = y },
                rcArea = new RECT { Left = x, Top = y, Right = x + 1, Bottom = y + Math.Max(cellHeight, 1) }
            };
            ImmSetCandidateWindow(context, ref candidates);
            return moved;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                ImmReleaseContext(hwnd, context);
            }
        }
    }
}
