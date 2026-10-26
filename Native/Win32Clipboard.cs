using System;
using System.Runtime.InteropServices;

namespace SmarTTY.Native;

public class Win32Clipboard
{
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
