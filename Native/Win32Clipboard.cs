using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

    // The clipboard formats this class asks for. A dead-code cleanup removed the larger enum this came from, but the
    // file-clipboard code below reads them by name.
    private enum ClipboardFormat : uint
    {
        UnicodeText = 13u,
        HDrop = 15u
    }

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

    /// <summary>
    /// Opens the clipboard, trying again for a moment: another program (a clipboard manager, a remote desktop client)
    /// often holds it for a few milliseconds after a copy, and a failed open used to look like an empty clipboard.
    /// </summary>
    private static bool OpenClipboardWithRetry(IntPtr hWnd)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(hWnd))
            {
                return true;
            }
            Thread.Sleep(15);
        }
        return false;
    }

    /// <summary>Whether the clipboard holds files (copied or cut in Explorer).</summary>
    public static bool HasFiles()
    {
        return IsClipboardFormatAvailable((uint)ClipboardFormat.HDrop);
    }

    /// <summary>Whether the clipboard holds text.</summary>
    public static bool HasText()
    {
        return IsClipboardFormatAvailable((uint)ClipboardFormat.UnicodeText);
    }

    /// <summary>The full paths of the files in the clipboard, or null when it holds none.</summary>
    public static string[] GetFiles(IntPtr hWnd)
    {
        if (!HasFiles() || !OpenClipboardWithRetry(hWnd))
        {
            return null;
        }
        try
        {
            IntPtr drop = GetClipboardData((uint)ClipboardFormat.HDrop);
            if (drop == IntPtr.Zero)
            {
                return null;
            }
            uint count = DragQueryFile(drop, 0xFFFFFFFFu, null, 0u);
            List<string> files = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(drop, i, null, 0u);
                StringBuilder path = new StringBuilder((int)length + 1);
                DragQueryFile(drop, i, path, length + 1);
                if (path.Length > 0)
                {
                    files.Add(path.ToString());
                }
            }
            return files.Count == 0 ? null : files.ToArray();
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static string GetText(IntPtr hWnd)
    {
        IntPtr lockedPtr = IntPtr.Zero;
        if (!OpenClipboardWithRetry(hWnd))
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
        if (!OpenClipboardWithRetry(hWnd))
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
