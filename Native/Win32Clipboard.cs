using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

public class Win32Clipboard
{
    private enum ClipboardFormat : uint
    {
        Text = 1u,
        Bitmap = 2u,
        MetafilePict = 3u,
        Sylk = 4u,
        Dif = 5u,
        Tiff = 6u,
        OemText = 7u,
        Dib = 8u,
        Palette = 9u,
        PenData = 10u,
        Riff = 11u,
        Wave = 12u,
        UnicodeText = 13u,
        EnhMetafile = 14u,
        HDrop = 15u,
        Locale = 16u,
        DibV5 = 17u,
        OwnerDisplay = 128u,
        DspText = 129u,
        DspBitmap = 130u,
        DspMetafilePict = 131u,
        DspEnhMetafile = 142u
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    public static string GetText(IntPtr hWnd)
    {
        IntPtr lockedPtr = IntPtr.Zero;
        if (!OpenClipboard(hWnd))
        {
            return null;
        }
        try
        {
            IntPtr clipboardData = GetClipboardData(13u);
            if (clipboardData == IntPtr.Zero)
            {
                return null;
            }
            lockedPtr = GlobalLock(clipboardData);
            return Marshal.PtrToStringUni(lockedPtr);
        }
        finally
        {
            if (lockedPtr != IntPtr.Zero)
            {
                GlobalUnlock(lockedPtr);
            }
            CloseClipboard();
        }
    }

    public static void SetText(IntPtr hWnd, string text)
    {
        if (!OpenClipboard(hWnd))
        {
            throw new InvalidOperationException("Cannot open clipboard, error " + Marshal.GetLastWin32Error());
        }
        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("EmptyClipboard() failed, error " + Marshal.GetLastWin32Error());
            }
            if (text == null)
            {
                text = "";
            }
            IntPtr hglobal = Marshal.StringToHGlobalUni(text);
            if (SetClipboardData(13u, hglobal) == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(hglobal);
                throw new InvalidOperationException("Cannot set clipboard data, error" + Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
