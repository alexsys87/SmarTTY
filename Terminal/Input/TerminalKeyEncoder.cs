using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Input;

/// <summary>What the key-down event of a view says about a key, before any character is known.</summary>
public enum TerminalKeyKind
{
    /// <summary>An ordinary key press.</summary>
    Normal,

    /// <summary>A dead key (an accent that waits for the next letter): the character comes with the next text input.</summary>
    DeadKey,

    /// <summary>A key that the input method (IME) took: its result comes as text input when the composition ends.</summary>
    ImeProcessed
}

/// <summary>A key press as the key-down event of a view reports it (WPF: <c>KeyEventArgs</c>, with <c>SystemKey</c> for Alt combinations).</summary>
public struct TerminalKeyEvent
{
    /// <summary>The key without modifiers.</summary>
    public VirtualKey Key;

    public bool Shift;

    public bool Control;

    public bool Alt;

    /// <summary>The right Alt key is held. Windows reports AltGr as the left Ctrl and the right Alt: with this, Ctrl+Alt is AltGr.</summary>
    public bool RightAlt;

    /// <summary>The extended flag of the scan code: the dedicated arrows, Insert, Home ... and the Enter of the keypad have it.</summary>
    public bool Extended;

    public TerminalKeyKind Kind;
}

/// <summary>What the view does with a key press.</summary>
public enum TerminalKeyAction
{
    /// <summary>Not a terminal key: the view leaves the event alone (the character, if any, comes as text input; the window may use the key).</summary>
    None,

    /// <summary>Send <see cref="TerminalKeyResult.Text"/> to the remote side; the event is handled (no text input follows it).</summary>
    Send,

    /// <summary>Paste the clipboard (Shift+Insert, Ctrl+Shift+V).</summary>
    Paste,

    /// <summary>Open the paste menu: the clipboard, the recent pastes and the snippets (Ctrl+Shift+Insert).</summary>
    PasteMenu,

    /// <summary>Copy the selection (Ctrl+Insert, Ctrl+Shift+C).</summary>
    Copy,

    /// <summary>Scroll the view one page up (Shift+PageUp, when the setting is on).</summary>
    ScrollPageUp,

    /// <summary>Scroll the view one page down (Shift+PageDown, when the setting is on).</summary>
    ScrollPageDown
}

/// <summary>The answer of <see cref="TerminalKeyEncoder.KeyDown"/>.</summary>
public struct TerminalKeyResult
{
    public TerminalKeyAction Action;

    /// <summary>The characters to send for <see cref="TerminalKeyAction.Send"/>.</summary>
    public string Text;

    public static readonly TerminalKeyResult None = default;

    public static TerminalKeyResult Send(string text)
    {
        return new TerminalKeyResult { Action = TerminalKeyAction.Send, Text = text };
    }

    public static TerminalKeyResult Do(TerminalKeyAction action)
    {
        return new TerminalKeyResult { Action = action };
    }

    public override string ToString()
    {
        return Action == TerminalKeyAction.Send ? "Send " + TerminalKeyEncoder.Describe(Text) : Action.ToString();
    }
}

/// <summary>The settings and the modes that change what a key sends. The view sets the modes from the emulator before every key.</summary>
public sealed class TerminalKeyOptions
{
    /// <summary>CSI ? 1 h: the arrows send ESC O A instead of ESC [ A.</summary>
    public bool ApplicationCursorKeys;

    /// <summary>ESC =: the keys of the keypad send escape sequences.</summary>
    public bool ApplicationKeypad;

    /// <summary>What Backspace sends (DEL by default); Ctrl+Backspace sends the other one of DEL and BS.</summary>
    public string BackspaceSequence = "\u007f";

    /// <summary>What Enter sends.</summary>
    public string EnterSequence = "\r";

    /// <summary>Alt with a character sends ESC and the character (the meta key of xterm); off: Alt+letter is left to the window (menus).</summary>
    public bool AltSendsEscape = true;

    /// <summary>Alt+F4 goes to the remote side instead of closing the window.</summary>
    public bool ForwardAltF4;

    /// <summary>Shift+PageUp and Shift+PageDown scroll the view instead of going to the remote side.</summary>
    public bool ShiftPageScrolls = true;

    /// <summary>Ctrl+Shift+C and Ctrl+Shift+V copy and paste instead of going to the remote side.</summary>
    public bool CtrlShiftClipboard = true;
}

/// <summary>
/// Turns the keyboard events of a view into what a terminal sends, as xterm does. It works with the two events that a WPF element gets for
/// a key press: the key-down event (<see cref="KeyDown"/>), which decides the keys that type no character (arrows, function keys, Enter, Tab,
/// Backspace, Esc, the Ctrl and Alt combinations with a fixed meaning), and the text input event (<see cref="TextInput"/>), which gets every
/// character the keyboard layout made, after the dead keys, the input method and the Alt codes. A key that <see cref="KeyDown"/> sends for
/// must be marked handled by the view: WPF then does not translate it into a character, so it is not sent twice.
/// <para>
/// The rules: a dead key and a key the IME took are never handled on key down; Ctrl+letter is taken by the position of the key (the virtual
/// key), so it works on any layout, Cyrillic included; AltGr (Ctrl with the right Alt) is left to the layout, so that AltGr+Q types '@' on a
/// German keyboard, while the left Ctrl with the left Alt sends ESC and the control character; Alt with digits of the keypad is a Windows Alt
/// code, so its keys send nothing and the character it makes is sent as typed (not with ESC); Alt with a character sends ESC and the
/// character of the layout (the system text of WPF).
/// </para>
/// </summary>
public sealed class TerminalKeyEncoder
{
    private const string Esc = "\u001b";

    private readonly XtermKeyTable _keys;

    // An Alt code is being typed (Alt with digits of the keypad): its character comes as text input when Alt is released.
    private bool _altCodePending;

    public TerminalKeyEncoder(XtermKeyTable keys)
    {
        _keys = keys;
    }

    /// <summary>An Alt code is being typed and its character has not come yet.</summary>
    public bool AltCodePending => _altCodePending;

    /// <summary>What a key-down event does.</summary>
    public TerminalKeyResult KeyDown(TerminalKeyEvent e, TerminalKeyOptions options)
    {
        if (e.Kind != TerminalKeyKind.Normal)
        {
            // The character comes as text input (after the accent or the composition); the key itself sends nothing.
            return TerminalKeyResult.None;
        }
        VirtualKey key = e.Key & VirtualKey.KeyCode;
        if (IsModifierKey(key))
        {
            return TerminalKeyResult.None;
        }
        bool altGr = e.Control && e.Alt && e.RightAlt;
        if (e.Alt && !e.Control && IsAltCodeKey(key, e.Extended))
        {
            _altCodePending = true;
            return TerminalKeyResult.None;
        }
        _altCodePending = false;

        // The keys of the view itself.
        if (!e.Alt)
        {
            if (key == VirtualKey.Insert && e.Shift && !e.Control && !IsKeypad(key, e.Extended, options))
            {
                return TerminalKeyResult.Do(TerminalKeyAction.Paste);
            }
            if (key == VirtualKey.Insert && e.Control && !e.Shift && !IsKeypad(key, e.Extended, options))
            {
                return TerminalKeyResult.Do(TerminalKeyAction.Copy);
            }
            if (options.CtrlShiftClipboard && e.Control && e.Shift && key == VirtualKey.C)
            {
                return TerminalKeyResult.Do(TerminalKeyAction.Copy);
            }
            if (key == VirtualKey.Insert && e.Control && e.Shift && !IsKeypad(key, e.Extended, options))
            {
                return TerminalKeyResult.Do(TerminalKeyAction.PasteMenu);
            }
            if (options.CtrlShiftClipboard && e.Control && e.Shift && key == VirtualKey.V)
            {
                return TerminalKeyResult.Do(TerminalKeyAction.Paste);
            }
            if (options.ShiftPageScrolls && e.Shift && !e.Control && (key == VirtualKey.PageUp || key == VirtualKey.PageDown) && !IsKeypad(key, e.Extended, options))
            {
                return TerminalKeyResult.Do(key == VirtualKey.PageUp ? TerminalKeyAction.ScrollPageUp : TerminalKeyAction.ScrollPageDown);
            }
        }
        if (key == VirtualKey.F4 && e.Alt && !e.Control && !e.Shift && !options.ForwardAltF4)
        {
            // The window closes.
            return TerminalKeyResult.None;
        }

        // The keys of the layout table: arrows, editing keys, function keys, the keypad in application mode, the Enter of the keypad.
        bool numPad = _keys.IsNumPadKey(key, e.Extended);
        string sequence = _keys.GetEscapeSequence(key, options.ApplicationKeypad, options.ApplicationCursorKeys, numPad);
        if (sequence != null)
        {
            // Ctrl+Left is ESC [ 1 ; 5 D (inserting ";5" into ESC [ D gave ESC [ ; 5 D, which shells and editors do not recognise).
            return TerminalKeyResult.Send(XtermProtocol.ApplyKeyModifiers(Esc + sequence, e.Shift, e.Alt, e.Control));
        }

        string altPrefix = (e.Alt && !altGr) ? Esc : "";
        switch (key)
        {
            case VirtualKey.Return:
                return TerminalKeyResult.Send(altPrefix + options.EnterSequence);
            case VirtualKey.Tab:
                if (e.Control || e.Alt)
                {
                    // Ctrl+Tab switches the tabs of the window.
                    return TerminalKeyResult.None;
                }
                return TerminalKeyResult.Send(e.Shift ? Esc + "[Z" : "\t");
            case VirtualKey.Escape:
                if (e.Alt || e.Control)
                {
                    return TerminalKeyResult.None;
                }
                return TerminalKeyResult.Send(Esc);
            case VirtualKey.Back:
            {
                string backspace = options.BackspaceSequence ?? "\u007f";
                if (e.Control)
                {
                    // Ctrl+Backspace sends the other erase character (BS when Backspace is DEL), as in xterm.
                    backspace = backspace == "\b" ? "\u007f" : "\b";
                }
                return TerminalKeyResult.Send(altPrefix + backspace);
            }
        }

        if (e.Control && !altGr)
        {
            char? control = ControlCharacter(key);
            if (control.HasValue)
            {
                return TerminalKeyResult.Send(altPrefix + control.Value);
            }
        }
        // Everything else types a character (or nothing): it comes as text input.
        return TerminalKeyResult.None;
    }

    /// <summary>
    /// What a text input event sends (null: nothing). <paramref name="text"/> is the typed text (after dead keys, the IME, AltGr),
    /// <paramref name="systemText"/> the character of an Alt combination, <paramref name="controlText"/> a control character that the
    /// layout made for a Ctrl combination that <see cref="KeyDown"/> did not take.
    /// </summary>
    public string TextInput(string text, string systemText, string controlText, TerminalKeyOptions options)
    {
        if (_altCodePending)
        {
            _altCodePending = false;
            // The character of an Alt code is typed text, whichever way the view reports it.
            string typed = !string.IsNullOrEmpty(text) ? text : systemText;
            return string.IsNullOrEmpty(typed) ? null : typed;
        }
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }
        if (!string.IsNullOrEmpty(systemText))
        {
            return options.AltSendsEscape ? Esc + systemText : null;
        }
        if (!string.IsNullOrEmpty(controlText))
        {
            return controlText;
        }
        return null;
    }

    /// <summary>Forgets an Alt code in progress (the view lost the focus).</summary>
    public void Reset()
    {
        _altCodePending = false;
    }

    private bool IsKeypad(VirtualKey key, bool extended, TerminalKeyOptions options)
    {
        return options.ApplicationKeypad && _keys.IsNumPadKey(key, extended);
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.ShiftKey:
            case VirtualKey.LShiftKey:
            case VirtualKey.RShiftKey:
            case VirtualKey.ControlKey:
            case VirtualKey.LControlKey:
            case VirtualKey.RControlKey:
            case VirtualKey.Menu:
            case VirtualKey.LMenu:
            case VirtualKey.RMenu:
            case VirtualKey.LWin:
            case VirtualKey.RWin:
            case VirtualKey.Apps:
            case VirtualKey.Capital:
            case VirtualKey.Scroll:
            case VirtualKey.ProcessKey:
            case VirtualKey.Packet:
            case VirtualKey.None:
                return true;
        }
        return false;
    }

    // The keys of an Alt code: the digits of the keypad, with NumLock on or off (then they are the editing keys without the extended flag).
    private static bool IsAltCodeKey(VirtualKey key, bool extended)
    {
        if (key >= VirtualKey.NumPad0 && key <= VirtualKey.NumPad9)
        {
            return true;
        }
        if (extended)
        {
            return false;
        }
        switch (key)
        {
            case VirtualKey.Insert:
            case VirtualKey.End:
            case VirtualKey.Down:
            case VirtualKey.PageDown:
            case VirtualKey.Left:
            case VirtualKey.Clear:
            case VirtualKey.Right:
            case VirtualKey.Home:
            case VirtualKey.Up:
            case VirtualKey.PageUp:
                return true;
        }
        return false;
    }

    /// <summary>
    /// The control character of Ctrl with a key, by the position of the key as in xterm: Ctrl+A..Z are 1..26, Ctrl+Space and Ctrl+2 (Ctrl+@)
    /// NUL, Ctrl+[ and Ctrl+3 ESC, Ctrl+\ and Ctrl+4 FS, Ctrl+] and Ctrl+5 GS, Ctrl+6 (Ctrl+^) RS, Ctrl+- Ctrl+/ and Ctrl+7 US, Ctrl+8 DEL.
    /// Null for the other keys.
    /// </summary>
    public static char? ControlCharacter(VirtualKey key)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            return (char)(key - VirtualKey.A + 1);
        }
        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.D2:
                return '\0';
            case VirtualKey.OemOpenBrackets:
            case VirtualKey.D3:
                return '\u001b';
            case VirtualKey.OemPipe:
            case VirtualKey.D4:
                return '\u001c';
            case VirtualKey.OemCloseBrackets:
            case VirtualKey.D5:
                return '\u001d';
            case VirtualKey.D6:
                return '\u001e';
            case VirtualKey.OemMinus:
            case VirtualKey.OemQuestion:
            case VirtualKey.D7:
                return '\u001f';
            case VirtualKey.D8:
                return '\u007f';
        }
        return null;
    }

    /// <summary>The characters of a key sequence with the control characters named (ESC [ A as "^[[A"), for logs and tests.</summary>
    public static string Describe(string text)
    {
        if (text == null)
        {
            return "null";
        }
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (ch < 0x20)
            {
                builder.Append('^').Append((char)(ch + 0x40));
            }
            else if (ch == 0x7f)
            {
                builder.Append("^?");
            }
            else
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }
}
