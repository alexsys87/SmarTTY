using SmarTTY.Terminal.Input;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;

// Checks of the keyboard of the WPF terminal view (Terminal/Input/): what a key-down event sends or leaves to the text input, and what a
// text input sends, with the key layout of the application (VisualTTY/XTermKeys.xml). The cases follow what a WPF element gets on
// Windows: the key of an Alt combination as SystemKey, a dead key and a key of the IME as such, AltGr as the left Ctrl with the right Alt,
// the character of an Alt combination as system text.
internal static class Program
{
    private static int _passed;

    private static XtermKeyTable _table;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    [Flags]
    private enum M
    {
        None = 0,
        Shift = 1,
        Ctrl = 2,
        Alt = 4,
        RightAlt = 8,
        Ext = 16,
        Dead = 32,
        Ime = 64
    }

    private static TerminalKeyEvent Key(VirtualKey key, M modifiers = M.None)
    {
        return new TerminalKeyEvent
        {
            Key = key,
            Shift = (modifiers & M.Shift) != 0,
            Control = (modifiers & M.Ctrl) != 0 || (modifiers & M.RightAlt) != 0,
            Alt = (modifiers & M.Alt) != 0 || (modifiers & M.RightAlt) != 0,
            RightAlt = (modifiers & M.RightAlt) != 0,
            Extended = (modifiers & M.Ext) != 0,
            Kind = (modifiers & M.Dead) != 0 ? TerminalKeyKind.DeadKey : ((modifiers & M.Ime) != 0 ? TerminalKeyKind.ImeProcessed : TerminalKeyKind.Normal)
        };
    }

    // The dedicated editing keys and arrows have the extended flag; the same keys of the keypad (NumLock off) do not.
    private static readonly HashSet<VirtualKey> ExtendedByDefault = new HashSet<VirtualKey>
    {
        VirtualKey.Up, VirtualKey.Down, VirtualKey.Left, VirtualKey.Right, VirtualKey.Home, VirtualKey.End,
        VirtualKey.Insert, VirtualKey.Delete, VirtualKey.PageUp, VirtualKey.PageDown
    };

    private static string Down(TerminalKeyEncoder encoder, TerminalKeyOptions options, VirtualKey key, M modifiers = M.None)
    {
        if (ExtendedByDefault.Contains(key))
        {
            modifiers |= M.Ext;
        }
        return encoder.KeyDown(Key(key, modifiers), options).ToString();
    }

    private static string DownRaw(TerminalKeyEncoder encoder, TerminalKeyOptions options, VirtualKey key, M modifiers = M.None)
    {
        return encoder.KeyDown(Key(key, modifiers), options).ToString();
    }

    private static string Text(TerminalKeyEncoder encoder, TerminalKeyOptions options, string text, string systemText = null, string controlText = null)
    {
        return TerminalKeyEncoder.Describe(encoder.TextInput(text, systemText, controlText, options));
    }

    private static int Main()
    {
        try
        {
            using (Stream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "XTermKeys.xml")))
            {
                _table = new XtermKeyTable(stream);
            }
            SpecialKeys();
            ApplicationModes();
            ViewKeys();
            EditingKeys();
            ControlKeys();
            AltAndAltGr();
            TextInputs();
            DeadKeysImeAndAltCodes();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL") ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }

    private static void SpecialKeys()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Down(e, o, VirtualKey.Up), "Send ^[[A", "Up");
        Equal(Down(e, o, VirtualKey.Down), "Send ^[[B", "Down");
        Equal(Down(e, o, VirtualKey.Right), "Send ^[[C", "Right");
        Equal(Down(e, o, VirtualKey.Left), "Send ^[[D", "Left");
        Equal(Down(e, o, VirtualKey.Home), "Send ^[[H", "Home");
        Equal(Down(e, o, VirtualKey.End), "Send ^[[F", "End");
        Equal(Down(e, o, VirtualKey.Insert), "Send ^[[2~", "Insert");
        Equal(Down(e, o, VirtualKey.Delete), "Send ^[[3~", "Delete");
        Equal(Down(e, o, VirtualKey.PageUp), "Send ^[[5~", "PageUp");
        Equal(Down(e, o, VirtualKey.PageDown), "Send ^[[6~", "PageDown");
        Equal(Down(e, o, VirtualKey.F1), "Send ^[OP", "F1");
        Equal(Down(e, o, VirtualKey.F4), "Send ^[OS", "F4");
        Equal(Down(e, o, VirtualKey.F5), "Send ^[[15~", "F5");
        Equal(Down(e, o, VirtualKey.F10), "Send ^[[21~", "F10 (WPF reports it as a system key; it must not open the menu)");
        Equal(Down(e, o, VirtualKey.F12), "Send ^[[24~", "F12");
        Equal(Down(e, o, VirtualKey.Up, M.Ctrl), "Send ^[[1;5A", "Ctrl+Up");
        Equal(Down(e, o, VirtualKey.Right, M.Shift | M.Alt), "Send ^[[1;4C", "Shift+Alt+Right");
        Equal(Down(e, o, VirtualKey.Left, M.Ctrl | M.Shift | M.Alt), "Send ^[[1;8D", "Ctrl+Shift+Alt+Left");
        Equal(Down(e, o, VirtualKey.F1, M.Shift), "Send ^[[1;2P", "Shift+F1");
        Equal(Down(e, o, VirtualKey.F5, M.Ctrl), "Send ^[[15;5~", "Ctrl+F5");
        Equal(Down(e, o, VirtualKey.Delete, M.Ctrl), "Send ^[[3;5~", "Ctrl+Delete");
        Equal(Down(e, o, VirtualKey.Home, M.Alt), "Send ^[[1;3H", "Alt+Home");
        Equal(Down(e, o, VirtualKey.F13), "None", "a function key the layout does not have");
        Equal(Down(e, o, VirtualKey.ShiftKey, M.Shift), "None", "Shift alone");
        Equal(Down(e, o, VirtualKey.ControlKey, M.Ctrl), "None", "Ctrl alone");
        Equal(Down(e, o, VirtualKey.Menu, M.Alt), "None", "Alt alone");
        Equal(Down(e, o, VirtualKey.LWin), "None", "the Windows key");
        Equal(Down(e, o, VirtualKey.Apps), "None", "the menu key");
        Equal(Down(e, o, VirtualKey.Capital), "None", "Caps Lock");
        Equal(Down(e, o, VirtualKey.A), "None", "a letter comes as text input");
        Equal(Down(e, o, VirtualKey.A, M.Shift), "None", "a capital letter comes as text input");
        Equal(Down(e, o, VirtualKey.Space), "None", "the blank comes as text input");
        Equal(Down(e, o, VirtualKey.NumPad5), "None", "a digit of the keypad comes as text input");
    }

    private static void ApplicationModes()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions { ApplicationCursorKeys = true };
        Equal(Down(e, o, VirtualKey.Up), "Send ^[OA", "application cursor keys: Up");
        Equal(Down(e, o, VirtualKey.Home), "Send ^[OH", "application cursor keys: Home");
        Equal(Down(e, o, VirtualKey.Up, M.Ctrl), "Send ^[[1;5A", "application cursor keys: Ctrl+Up has the CSI form");
        Equal(Down(e, o, VirtualKey.PageUp), "Send ^[[5~", "application cursor keys: PageUp does not change");

        o = new TerminalKeyOptions { ApplicationKeypad = true };
        Equal(Down(e, o, VirtualKey.NumPad5), "Send ^[Ou", "application keypad: 5");
        Equal(Down(e, o, VirtualKey.NumPad0), "Send ^[Op", "application keypad: 0");
        Equal(Down(e, o, VirtualKey.Add), "Send ^[Ol", "application keypad: +");
        Equal(Down(e, o, VirtualKey.Subtract), "Send ^[OS", "application keypad: -");
        Equal(Down(e, o, VirtualKey.Decimal), "Send ^[On", "application keypad: .");
        Equal(DownRaw(e, o, VirtualKey.Return, M.Ext), "Send ^[OM", "application keypad: the Enter of the keypad");
        Equal(DownRaw(e, o, VirtualKey.Return), "Send ^M", "application keypad: the main Enter is still CR");
        Equal(DownRaw(e, o, VirtualKey.Insert), "Send ^[Op", "application keypad: 0 with NumLock off (Insert without the extended flag)");
        Equal(Down(e, o, VirtualKey.Insert), "Send ^[[2~", "application keypad: the dedicated Insert");
        Equal(DownRaw(e, o, VirtualKey.Up), "Send ^[Ox", "application keypad: 8 with NumLock off");
        Equal(Down(e, o, VirtualKey.Up), "Send ^[[A", "application keypad: the dedicated Up");
        Equal(DownRaw(e, o, VirtualKey.Insert, M.Shift), "Send ^[Op", "application keypad: Shift with the 0 of the keypad does not paste");

        o = new TerminalKeyOptions();
        Equal(DownRaw(e, o, VirtualKey.Return, M.Ext), "Send ^M", "the Enter of the keypad is CR in the normal mode");
        Equal(DownRaw(e, o, VirtualKey.Up), "Send ^[[A", "8 with NumLock off is Up in the normal mode");
    }

    private static void ViewKeys()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Down(e, o, VirtualKey.Insert, M.Shift), "Paste", "Shift+Insert pastes");
        Equal(Down(e, o, VirtualKey.Insert, M.Ctrl), "Copy", "Ctrl+Insert copies");
        Equal(Down(e, o, VirtualKey.Insert, M.Ctrl | M.Shift), "PasteMenu", "Ctrl+Shift+Insert opens the paste menu");
        Equal(Down(e, o, VirtualKey.C, M.Ctrl | M.Shift), "Copy", "Ctrl+Shift+C copies");
        Equal(Down(e, o, VirtualKey.V, M.Ctrl | M.Shift), "Paste", "Ctrl+Shift+V pastes");
        Equal(Down(e, o, VirtualKey.PageUp, M.Shift), "ScrollPageUp", "Shift+PageUp scrolls the view");
        Equal(Down(e, o, VirtualKey.PageDown, M.Shift), "ScrollPageDown", "Shift+PageDown scrolls the view");
        Equal(Down(e, o, VirtualKey.F4, M.Alt), "None", "Alt+F4 is left to the window");
        Equal(Down(e, o, VirtualKey.Tab, M.Ctrl), "None", "Ctrl+Tab is left to the window (tabs)");
        Equal(Down(e, o, VirtualKey.Escape, M.Alt), "None", "Alt+Esc is left to Windows");

        o = new TerminalKeyOptions { CtrlShiftClipboard = false, ShiftPageScrolls = false, ForwardAltF4 = true };
        Equal(Down(e, o, VirtualKey.C, M.Ctrl | M.Shift), "Send ^C", "without the clipboard keys Ctrl+Shift+C is ^C");
        Equal(Down(e, o, VirtualKey.PageUp, M.Shift), "Send ^[[5;2~", "without scrolling Shift+PageUp goes to the program");
        Equal(Down(e, o, VirtualKey.F4, M.Alt), "Send ^[[1;3S", "Alt+F4 forwarded");
    }

    private static void EditingKeys()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Down(e, o, VirtualKey.Return), "Send ^M", "Enter");
        Equal(Down(e, o, VirtualKey.Return, M.Alt), "Send ^[^M", "Alt+Enter");
        Equal(Down(e, o, VirtualKey.Return, M.Shift), "Send ^M", "Shift+Enter");
        Equal(Down(e, o, VirtualKey.Tab), "Send ^I", "Tab");
        Equal(Down(e, o, VirtualKey.Tab, M.Shift), "Send ^[[Z", "Shift+Tab is the back tab");
        Equal(Down(e, o, VirtualKey.Escape), "Send ^[", "Esc");
        Equal(Down(e, o, VirtualKey.Back), "Send ^?", "Backspace is DEL");
        Equal(Down(e, o, VirtualKey.Back, M.Ctrl), "Send ^H", "Ctrl+Backspace is BS");
        Equal(Down(e, o, VirtualKey.Back, M.Alt), "Send ^[^?", "Alt+Backspace (delete a word in readline)");
        Equal(Down(e, o, VirtualKey.Back, M.Shift), "Send ^?", "Shift+Backspace");
        o = new TerminalKeyOptions { BackspaceSequence = "\b", EnterSequence = "\r\n" };
        Equal(Down(e, o, VirtualKey.Back), "Send ^H", "Backspace set to BS");
        Equal(Down(e, o, VirtualKey.Back, M.Ctrl), "Send ^?", "then Ctrl+Backspace is DEL");
        Equal(Down(e, o, VirtualKey.Return), "Send ^M^J", "Enter set to CR LF");
    }

    private static void ControlKeys()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Down(e, o, VirtualKey.A, M.Ctrl), "Send ^A", "Ctrl+A");
        Equal(Down(e, o, VirtualKey.C, M.Ctrl), "Send ^C", "Ctrl+C (by the position of the key: the same on a Cyrillic layout)");
        Equal(Down(e, o, VirtualKey.Z, M.Ctrl), "Send ^Z", "Ctrl+Z");
        Equal(Down(e, o, VirtualKey.D, M.Ctrl | M.Shift), "Send ^D", "Ctrl+Shift+D is ^D, as in xterm");
        Equal(Down(e, o, VirtualKey.Space, M.Ctrl), "Send ^@", "Ctrl+Space is NUL");
        Equal(Down(e, o, VirtualKey.D2, M.Ctrl | M.Shift), "Send ^@", "Ctrl+@ is NUL");
        Equal(Down(e, o, VirtualKey.D2, M.Ctrl), "Send ^@", "Ctrl+2 is NUL");
        Equal(Down(e, o, VirtualKey.OemOpenBrackets, M.Ctrl), "Send ^[", "Ctrl+[ is ESC");
        Equal(Down(e, o, VirtualKey.D3, M.Ctrl), "Send ^[", "Ctrl+3 is ESC");
        Equal(Down(e, o, VirtualKey.OemPipe, M.Ctrl), "Send ^\\", "Ctrl+\\ is FS (SIGQUIT)");
        Equal(Down(e, o, VirtualKey.OemCloseBrackets, M.Ctrl), "Send ^]", "Ctrl+] is GS (telnet's escape)");
        Equal(Down(e, o, VirtualKey.D6, M.Ctrl | M.Shift), "Send ^^", "Ctrl+^ is RS");
        Equal(Down(e, o, VirtualKey.OemMinus, M.Ctrl | M.Shift), "Send ^_", "Ctrl+_ is US (undo in Emacs)");
        Equal(Down(e, o, VirtualKey.OemQuestion, M.Ctrl), "Send ^_", "Ctrl+/ is US");
        Equal(Down(e, o, VirtualKey.D8, M.Ctrl), "Send ^?", "Ctrl+8 is DEL");
        Equal(Down(e, o, VirtualKey.D1, M.Ctrl), "None", "Ctrl+1 has no control character");
        Equal(Down(e, o, VirtualKey.OemPeriod, M.Ctrl), "None", "Ctrl+. has no control character");
    }

    private static void AltAndAltGr()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Down(e, o, VirtualKey.A, M.Ctrl | M.Alt), "Send ^[^A", "the left Ctrl with the left Alt: ESC and the control character");
        Equal(Down(e, o, VirtualKey.Q, M.RightAlt), "None", "AltGr+Q is left to the layout ('@' on a German keyboard)");
        Equal(Text(e, o, "@"), "@", "and its character is typed");
        Equal(Down(e, o, VirtualKey.D7, M.RightAlt), "None", "AltGr+7 is left to the layout ('{' on a German keyboard)");
        Equal(Down(e, o, VirtualKey.Space, M.RightAlt), "None", "AltGr+Space is left to the layout");
        Equal(Down(e, o, VirtualKey.Up, M.RightAlt), "Send ^[[1;7A", "AltGr with an arrow is Ctrl+Alt+arrow");
        Equal(Down(e, o, VirtualKey.Return, M.RightAlt), "Send ^M", "AltGr+Enter is Enter");
        Equal(Down(e, o, VirtualKey.X, M.Alt), "None", "Alt+X: the character comes as system text");
        Equal(Text(e, o, null, "x"), "^[x", "Alt+X sends ESC x");
        Equal(Text(e, o, null, "X"), "^[X", "Alt+Shift+X sends ESC X");
        Equal(Text(e, o, null, "ф"), "^[ф", "Alt with a Cyrillic letter sends ESC and the letter of the layout");
        Equal(Down(e, o, VirtualKey.Space, M.Alt), "None", "Alt+Space is left to the window (its system menu)");
        o = new TerminalKeyOptions { AltSendsEscape = false };
        Equal(Text(e, o, null, "x"), "null", "with Alt for the menus Alt+X sends nothing");
    }

    private static void TextInputs()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(Text(e, o, "a"), "a", "a letter");
        Equal(Text(e, o, "ф"), "ф", "a Cyrillic letter");
        Equal(Text(e, o, "😀"), "😀", "a character outside the BMP comes whole");
        Equal(Text(e, o, null, null, "\u0001"), "^A", "a control character that the layout made");
        Equal(Text(e, o, null, null, null), "null", "nothing");
        Equal(Text(e, o, "", "", ""), "null", "empty texts");
    }

    private static void DeadKeysImeAndAltCodes()
    {
        TerminalKeyEncoder e = new TerminalKeyEncoder(_table);
        TerminalKeyOptions o = new TerminalKeyOptions();
        Equal(DownRaw(e, o, VirtualKey.Oem7, M.Dead), "None", "a dead key sends nothing");
        Equal(Text(e, o, "é"), "é", "the letter it made is typed");
        Equal(DownRaw(e, o, VirtualKey.Oem7, M.Dead | M.Shift), "None", "a dead key with Shift");
        Equal(Text(e, o, "¨"), "¨", "a dead key followed by a blank types the accent");
        Equal(DownRaw(e, o, VirtualKey.Return, M.Ime), "None", "Enter that ends an IME composition is not sent");
        Equal(DownRaw(e, o, VirtualKey.Back, M.Ime), "None", "Backspace inside an IME composition is not sent");
        Equal(DownRaw(e, o, VirtualKey.Escape, M.Ime), "None", "Esc that cancels an IME composition is not sent");
        Equal(Text(e, o, "日本語"), "日本語", "the result of the composition is typed");

        Equal(DownRaw(e, o, VirtualKey.NumPad0, M.Alt), "None", "Alt+0 of the keypad starts an Alt code");
        Check(e.AltCodePending, "an Alt code is pending");
        Equal(DownRaw(e, o, VirtualKey.NumPad2, M.Alt), "None", "Alt+2");
        Equal(DownRaw(e, o, VirtualKey.NumPad3, M.Alt), "None", "Alt+3");
        Equal(DownRaw(e, o, VirtualKey.NumPad3, M.Alt), "None", "Alt+3");
        Equal(Text(e, o, null, "é"), "é", "the character of the Alt code is typed without ESC (as system text)");
        Check(!e.AltCodePending, "the Alt code is over");
        DownRaw(e, o, VirtualKey.NumPad6, M.Alt);
        DownRaw(e, o, VirtualKey.NumPad5, M.Alt);
        Equal(Text(e, o, "A"), "A", "the character of the Alt code as text");
        Equal(Text(e, o, null, "x"), "^[x", "after it Alt+X sends ESC x again");
        Equal(DownRaw(e, o, VirtualKey.Insert, M.Alt), "None", "Alt with 0 of the keypad with NumLock off starts an Alt code");
        Equal(DownRaw(e, o, VirtualKey.End, M.Alt), "None", "Alt with 1 of the keypad with NumLock off");
        Equal(Text(e, o, null, "☺"), "☺", "its character is typed");
        Equal(Down(e, o, VirtualKey.Insert, M.Alt), "Send ^[[2;3~", "Alt with the dedicated Insert is a key");
        Check(!e.AltCodePending, "and no Alt code");
        DownRaw(e, o, VirtualKey.NumPad1, M.Alt);
        Equal(Down(e, o, VirtualKey.Up, M.Ctrl), "Send ^[[1;5A", "another key ends an Alt code");
        Check(!e.AltCodePending, "the Alt code was dropped");
        DownRaw(e, o, VirtualKey.NumPad1, M.Alt);
        e.Reset();
        Check(!e.AltCodePending, "a reset (lost focus) drops an Alt code");
        Equal(DownRaw(e, o, VirtualKey.NumPad1, M.Alt | M.Ctrl), "None", "Ctrl+Alt with a digit of the keypad is no Alt code");
        Check(!e.AltCodePending, "no Alt code with Ctrl");
    }
}
