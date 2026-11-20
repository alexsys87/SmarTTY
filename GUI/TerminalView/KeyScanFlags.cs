using SmarTTY.TextEditing;
using System;
using System.Windows.Interop;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The extended flag of the key that is being pressed. WPF does not give it to the key-down event (the dedicated arrows and Insert, Home ...
/// and the same keys of the keypad with NumLock off have one virtual key; the scan code tells them apart, and the terminal needs that for
/// the keypad in application mode and for the Alt codes). The key messages are seen before WPF raises its event: the filter of the thread's
/// messages runs before the hook that makes the WPF key events (both are events of <see cref="ComponentDispatcher"/>).
/// </summary>
internal static class KeyScanFlags
{
    private const int WM_KEYDOWN = 0x0100;

    private const int WM_SYSKEYDOWN = 0x0104;

    [ThreadStatic]
    private static bool _installed;

    [ThreadStatic]
    private static int _lastVirtualKey;

    [ThreadStatic]
    private static bool _lastExtended;

    /// <summary>Starts watching the key messages of the calling thread (once per thread).</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }
        _installed = true;
        ComponentDispatcher.ThreadFilterMessage += OnFilterMessage;
    }

    private static void OnFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == WM_KEYDOWN || msg.message == WM_SYSKEYDOWN)
        {
            _lastVirtualKey = (int)msg.wParam.ToInt64() & 0xFF;
            _lastExtended = (msg.lParam.ToInt64() & 0x1000000) != 0;
        }
    }

    /// <summary>Whether the key that is being pressed has the extended flag (false when its message was not seen).</summary>
    public static bool IsExtended(VirtualKey key)
    {
        return _installed && _lastVirtualKey == ((int)key & 0xFF) && _lastExtended;
    }
}
