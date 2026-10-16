using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public partial class TerminalEmulatorControl : TerminalEmulatorBase
{
    public enum BeepMode
    {
        None,
        [EnumDisplayText("Default system beep")]
        SystemBeep,
        [EnumDisplayText("Beep through PC speaker")]
        ConsoleBeep,
        [EnumDisplayText("Custom")]
        WaveFile
    }

    internal class EscapeTrace : IDisposable
    {
        private string _warning;

        private string _description;

        private string _raw;

        private string _input;

        private int _position;

        private TerminalEmulatorControl _terminal;

        public EscapeTrace(TerminalEmulatorControl terminalEmulatorControl)
        {
            _terminal = terminalEmulatorControl;
        }

        public void Dispose()
        {
            if (_description != null)
            {
                _terminal.Trace("[ESC] " + _description);
            }
            else if (_warning != null)
            {
                _terminal.Trace("[ESC] WARNING: " + _warning + " (" + _raw + " )");
            }
            else if (_raw != null)
            {
                _terminal.Trace(_raw);
            }
            else
            {
                _terminal.Trace("???");
            }
        }

        internal void SetInput(string text, int value2)
        {
            _input = text;
            _position = value2;
        }

        internal void SetDescription(string text)
        {
            _description = text;
        }

        internal void SetWarning(string text)
        {
            _warning = text;
        }

        internal void SetRaw(string text)
        {
            this._raw = text;
        }
    }

    public delegate void TerminalSizeChangedHandler(TerminalEmulatorControl terminalEmulatorControl, int value2, int value3);

    public enum InputKind
    {
        Typed,
        KeySequence,
        MouseReport,
        Paste
    }

    public delegate void TerminalDataHandler(object obj, byte[] buffer, InputKind inputKind);

    public enum MouseReportingMode
    {
        Off = 0,
        X10 = 9,
        Normal = 1000,
        Highlight = 1001,
        ButtonEvent = 1002,
        AnyEvent = 1003
    }

    private XTermKeyMap _keyMap;

    public const string TerminalType = "xterm";

    public GlobalSettings AppSettings;

    public static readonly string ResetSequence = "\u001b[0m";

    private string _incompleteEscape;

    private Point _savedCursor;

    private bool _applicationKeypad;

    private bool _applicationCursorKeys;

    private bool[] _privateModes = new bool[1100];

    private bool[] _savedPrivateModes = new bool[1100];

    private bool _keyMappingEnabled = true;

    private bool _extendedKey;

    public string BackspaceSequence = new string('\u007f', 1);

    private MouseReportingMode _mouseMode;

    private ContextMenuStrip _normalContextMenu;

    private Point _lastMouseCell = new Point(-1);

    private int _pendingFlushes;

    private object _outputLock = new object();

    private string _pendingOutput;

    private FileStream _logStream;

    private int _tabWidth = 8;

    public static readonly Color DefaultTextColor = Color.LightGray;

    public bool ShowUnrecognizedAsHex { get; set; }

    public int MaxChunkLength { get; set; }

    [Category("Terminal")]
    public BeepMode BellMode { get; set; }

    [Category("Terminal")]
    public string BellSoundFile { get; set; }

    public TerminalEventDispatcher Dispatcher { get; set; }

    public bool ForwardAltF4 { get; set; }

    protected virtual bool UnusedOption => false;

    public string EnterSequence { get; set; }

    protected virtual bool UsesTerminalInput { get; } = true;

    public MouseReportingMode MouseMode
    {
        get
        {
            return _mouseMode;
        }
        set
        {
            if (value != _mouseMode)
            {
                _mouseMode = value;
                Cursor = ((value != MouseReportingMode.Off) ? Cursors.Arrow : Cursors.IBeam);
                _lastMouseCell = new Point(-1);
                if (value == MouseReportingMode.Off)
                {
                    ContextMenuStrip = _normalContextMenu;
                    return;
                }
                _normalContextMenu = ContextMenuStrip;
                ContextMenuStrip = null;
            }
        }
    }

    private static int MouseModifierBits
    {
        get
        {
            int num = 0;
            if ((Control.ModifierKeys & Keys.Shift) != 0)
            {
                num |= 4;
            }
            if ((Control.ModifierKeys & Keys.Alt) != 0)
            {
                num |= 8;
            }
            if ((Control.ModifierKeys & Keys.Control) != 0)
            {
                num |= 0x10;
            }
            return num;
        }
    }

    public int TabWidth
    {
        get
        {
            return _tabWidth;
        }
        set
        {
            _tabWidth = value;
        }
    }

    public bool CaptureDialogKeys { get; set; }

    public int UnusedValue { get; set; }

    public event TerminalSizeChangedHandler TerminalSizeChanged;

    public event TerminalDataHandler DataEntered;

    public TerminalEmulatorControl()
    {
        InitializeComponent();
        ((TextEditorControl)this).CopyLineWhenNoSelection = false;
        try
        {
            _keyMap = new XTermKeyMap();
        }
        catch
        {
        }
    }

    protected override bool IsWordChar(char ch)
    {
        if (ch == '.' || ch == '-')
        {
            return true;
        }
        return base.IsWordChar(ch);
    }

    public void ProcessOutput(string text2)
    {
        if (_incompleteEscape != null)
        {
            text2 = _incompleteEscape + text2;
            _incompleteEscape = null;
        }
        if (text2 == null)
        {
            return;
        }
        if (MaxChunkLength != 0 && text2.Length > MaxChunkLength)
        {
            text2 = $"\r\n--- skipped {text2.Length - MaxChunkLength} bytes ---\r\n" + text2.Substring(text2.Length - MaxChunkLength);
            GC.Collect();
        }
        int num = 0;
        try
        {
            BeginBatch();
            for (int num2 = 0; num2 < text2.Length; num2++)
            {
                if (text2[num2] >= ' ')
                {
                    continue;
                }
                WriteText(text2.Substring(num, num2 - num));
                if (text2[num2] != '\u001b')
                {
                    base.ResetLinkTracking();
                }
                num = num2 + 1;
                EscapeTrace escapeTrace = null;
                switch (text2[num2])
                {
                    case '\r':
                        Trace("<CR>");
                        MoveCursorBy(-base.CursorPosition.X, 0);
                        break;
                    case '\n':
                        Trace("<LF>");
                        MoveCursorBy(0, 1, flag: true);
                        break;
                    case '\t':
                        Trace("<TAB>");
                        MoveCursorBy(Tabs.NextDistance(base.CursorPosition.X, _tabWidth), 0);
                        break;
                    case '\a':
                        Trace("<BEL>");
                        OnBell();
                        break;
                    case '\b':
                        Trace("<BACK>");
                        if (base.CursorPosition.X == 0)
                        {
                            MoveCursorBy(_visibleColumns - 1, -1);
                        }
                        else
                        {
                            MoveCursorBy(-1, 0);
                        }
                        break;
                    case '\u001b':
                        {
                            int num3 = ParseEscapeSequence(text2, num2, out var text, escapeTrace);
                            num2 += num3 - 1;
                            num += num3 - 1;
                            if (text != null)
                            {
                                WriteText(text);
                            }
                            break;
                        }
                    default:
                        if (ShowUnrecognizedAsHex)
                        {
                            WriteText($"\\{(int)text2[num2]}");
                        }
                        break;
                }
            }
            if (num < text2.Length)
            {
                WriteText(text2.Substring(num, text2.Length - num));
            }
        }
        finally
        {
            EndBatch();
        }
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(string text, UIntPtr handle, uint value2);

    public static string ForegroundCode(int value2)
    {
        return $"{'\u001b'}[{30 + value2}m";
    }

    public static string Colorize(string text, int value2)
    {
        return ForegroundCode(value2) + text + ResetSequence;
    }

    private void PlayBell()
    {
        switch (BellMode)
        {
            case BeepMode.ConsoleBeep:
                Console.Beep();
                break;
            case BeepMode.WaveFile:
                if (BellSoundFile != null)
                {
                    try
                    {
                        new SoundPlayer(BellSoundFile).PlaySync();
                        break;
                    }
                    catch
                    {
                        break;
                    }
                }
                break;
            case BeepMode.SystemBeep:
                PlaySound(".Default", UIntPtr.Zero, 65536u);
                break;
        }
    }

    protected virtual void OnBell()
    {
        if (Dispatcher == null)
        {
            PlayBell();
        }
        else
        {
            Dispatcher.PostBeep(PlayBell);
        }
    }

    private static int[] ParseParameters(string[] items, params int[] items2)
    {
        int[] array = new int[Math.Max(items.Length, items2.Length)];
        for (int num = 0; num < array.Length; num++)
        {
            if (num >= items.Length || !int.TryParse(items[num], out array[num]))
            {
                array[num] = items2[num];
            }
        }
        return array;
    }

    private int ParseEscapeSequence(string text, int value2, out string text2, EscapeTrace escapeTrace = null)
    {
        text2 = null;
        escapeTrace?.SetInput(text, value2);
        if (value2 + 1 >= text.Length)
        {
            _incompleteEscape = text.Substring(value2);
            return 1;
        }
        switch (text[value2 + 1])
        {
            case ']':
                {
                    int num = AnsiEscapeUtils.FindOscEnd(text, value2 + 1, out var num2);
                    if (num == -1)
                    {
                        escapeTrace?.SetWarning("Invalid ST/BEL");
                        _incompleteEscape = text.Substring(value2);
                        return _incompleteEscape.Length;
                    }
                    ExecuteOsc(text.Substring(value2 + 2, num - value2 - 2).Split(';'), escapeTrace);
                    return num2 + 1;
                }
            case '[':
                {
                    int num = AnsiEscapeUtils.FindCsiEnd(text, value2 + 2);
                    if (num == -1)
                    {
                        escapeTrace?.SetWarning("Invalid CSI");
                        _incompleteEscape = text.Substring(value2);
                        return _incompleteEscape.Length;
                    }
                    int num3 = 0;
                    if (text[value2 + 2] == '?')
                    {
                        num3 = 1;
                    }
                    ExecuteCsi(text[num], text.Substring(value2 + 2 + num3, num - value2 - 2 - num3).Split(';'), num3 != 0, escapeTrace);
                    return num - value2 + 1;
                }
            case ' ':
            case '#':
            case '%':
            case '(':
            case ')':
            case '*':
            case '+':
                if (text.Length - value2 < 3)
                {
                    _incompleteEscape = text.Substring(value2);
                    return _incompleteEscape.Length;
                }
                escapeTrace?.SetRaw("[ESC]" + text.Substring(value2, 2));
                return 3;
            case '>':
                _applicationKeypad = false;
                escapeTrace?.SetDescription("DECPNM");
                return 2;
            case '=':
                _applicationKeypad = true;
                escapeTrace?.SetDescription("DECPNM");
                return 2;
            case '7':
                escapeTrace?.SetDescription("Save cursor");
                _savedCursor = base.CursorPosition;
                return 2;
            case '8':
                escapeTrace?.SetDescription("Restore cursor");
                base.CursorPosition = _savedCursor;
                return 2;
            case 'H':
                escapeTrace?.SetDescription("Set tab");
                Tabs.SetStop(base.CursorPosition.X);
                return 2;
            case 'c':
                escapeTrace?.SetDescription("Reset");
                Reset();
                return 2;
            case 'D':
                escapeTrace?.SetDescription("Remove lines");
                if (base.HasScrollRegion && base.CursorPosition.Y == ScrollBottom)
                {
                    ScrollRegionUp(base.ScrollTop, 1);
                }
                else
                {
                    MoveCursorBy(0, 1, flag: true);
                }
                return 2;
            case 'M':
                escapeTrace?.SetDescription("Add lines");
                if (base.CursorPosition.Y == base.ScrollTop)
                {
                    InsertBlankLines(base.ScrollTop, 1);
                }
                else
                {
                    MoveCursorBy(0, -1, flag: true);
                }
                return 2;
            case 'E':
                escapeTrace?.SetDescription("Got to next line");
                MoveCursorBy(-base.CursorPosition.X, 1, flag: true);
                return 2;
            default:
                return 2;
        }
    }

    internal bool ExecuteCsi(char ch, string[] items, bool flag, EscapeTrace escapeTrace = null)
    {
        escapeTrace?.SetRaw(string.Format("Unknown CSI command: {2}{0}, DEC={1}", ch, flag, string.Join(";", items)));
        int result = 0;
        int num = 1;
        if (items.Length != 0 && int.TryParse(items[0], out result))
        {
            num = result;
        }
        switch (ch)
        {
            case '@':
                InsertChars(num);
                escapeTrace?.SetDescription($"Insert char at {num}");
                return true;
            case 'A':
                escapeTrace?.SetDescription($"Go up {num}");
                MoveCursorBy(0, -num);
                return true;
            case 'B':
                escapeTrace?.SetDescription($"Go down {num}");
                MoveCursorBy(0, num);
                return true;
            case 'C':
                escapeTrace?.SetDescription($"Go left {num}");
                MoveCursorBy(num, 0);
                return true;
            case 'D':
                escapeTrace?.SetDescription($"Go right {num}");
                MoveCursorBy(-num, 0);
                return true;
            case 'E':
                escapeTrace?.SetDescription($"Go to start of relative line {num}");
                MoveCursorBy(-base.CursorPosition.X, num);
                return true;
            case 'F':
                escapeTrace?.SetDescription($"Go to start of relative line -{num}");
                MoveCursorBy(-base.CursorPosition.X, -num);
                return true;
            case 'G':
            case '`':
                escapeTrace?.SetDescription($"Set X= {num}");
                base.CursorPosition = new Point(num - 1, base.CursorPosition.Y);
                return true;
            case 'H':
            case 'f':
                {
                    int[] array2 = ParseParameters(items, 1, 1);
                    escapeTrace?.SetDescription($"GOTOXY {array2[1]}, {array2[0]}");
                    base.CursorPosition = new Point(array2[1] - 1, array2[0] - 1);
                    return true;
                }
            case 'd':
                escapeTrace?.SetDescription($"Set Y= {num}");
                base.CursorPosition = new Point(base.CursorPosition.X, num - 1);
                return true;
            case 'I':
                {
                    escapeTrace?.SetDescription($"Go {num} tabs");
                    for (int num4 = 0; num4 < num; num4++)
                    {
                        MoveCursorBy(Tabs.NextDistance(base.CursorPosition.X, _tabWidth), 0);
                    }
                    return true;
                }
            case 'J':
                escapeTrace?.SetDescription($"Erase terminal {(EraseMode)result}");
                if ((uint)result <= 2u)
                {
                    EraseInDisplay((EraseMode)result);
                }
                InvalidateView();
                return true;
            case 'K':
                escapeTrace?.SetDescription($"Erase line {(EraseMode)result}");
                if ((uint)result <= 2u)
                {
                    EraseInLine((EraseMode)result);
                }
                InvalidateView();
                return true;
            case 'L':
                escapeTrace?.SetDescription($"Insert {num} lines");
                InsertBlankLines(base.CursorPosition.Y, num);
                return true;
            case 'M':
                escapeTrace?.SetDescription($"Remove {num} lines");
                ScrollRegionUp(base.CursorPosition.Y, num);
                return true;
            case 'P':
                escapeTrace?.SetDescription($"Erase {num} chars");
                DeleteChars(num);
                return true;
            case 'S':
                escapeTrace?.SetDescription($"Remove {num} lines from clip region top");
                ScrollRegionUp(base.ScrollTop, num);
                return true;
            case 'T':
                escapeTrace?.SetDescription($"Insert {num} lines to clip region top");
                InsertBlankLines(base.ScrollTop, num);
                return true;
            case 'X':
                escapeTrace?.SetDescription($"Pad {num} chars");
                WriteText(new string(' ', num), flag: false);
                return true;
            case 'Z':
                {
                    escapeTrace?.SetDescription($"Go {num} tabs back");
                    for (int num3 = 0; num3 < num; num3++)
                    {
                        MoveCursorBy(-Tabs.PreviousDistance(base.CursorPosition.X), 0);
                    }
                    return true;
                }
            case 'b':
                escapeTrace?.SetDescription("Clone char");
                RepeatPreviousChar(num);
                return true;
            case 'g':
                escapeTrace?.SetDescription("Clear/reset tab");
                switch (result)
                {
                    case 0:
                        Tabs.ClearStop(base.CursorPosition.X);
                        break;
                    case 3:
                        Tabs.ClearAll();
                        break;
                }
                return true;
            case 'm':
                {
                    string[] array = items;
                    for (int num2 = 0; num2 < array.Length; num2++)
                    {
                        int.TryParse(array[num2], out var result6);
                        if (result6 >= 30 && result6 <= 37)
                        {
                            CurrentStyle.Foreground = (byte)(1 + result6 - 30);
                            continue;
                        }
                        if (result6 >= 40 && result6 <= 47)
                        {
                            CurrentStyle.Background = (byte)(1 + result6 - 40);
                            continue;
                        }
                        switch (result6)
                        {
                            case 0:
                                CurrentStyle = TerminalEmulatorBase.CellStyle.Default;
                                continue;
                            case 1:
                                CurrentStyle.Bold = true;
                                continue;
                            case 21:
                                CurrentStyle.Bold = false;
                                continue;
                            case 7:
                                CurrentStyle.Inverse = true;
                                continue;
                            case 27:
                                CurrentStyle.Inverse = false;
                                continue;
                            case 39:
                                CurrentStyle.Foreground = 0;
                                continue;
                            case 49:
                                CurrentStyle.Background = 0;
                                continue;
                            case 90:
                            case 91:
                            case 92:
                            case 93:
                            case 94:
                            case 95:
                            case 96:
                            case 97:
                                CurrentStyle.Foreground = (byte)(1 + result6 - 90);
                                CurrentStyle.Bold = true;
                                continue;
                        }
                        if (result6 >= 100 && result6 <= 107)
                        {
                            CurrentStyle.Background = (byte)(1 + result6 - 100);
                            CurrentStyle.Bold = true;
                        }
                    }
                    escapeTrace?.SetDescription($"SGR: {CurrentStyle}");
                    return true;
                }
            case 'h':
            case 'l':
                {
                    string[] array = items;
                    for (int num2 = 0; num2 < array.Length; num2++)
                    {
                        if (int.TryParse(array[num2], out var result5))
                        {
                            SetMode(result5, flag, ch == 'h', escapeTrace);
                            if (flag && result5 < _privateModes.Length)
                            {
                                _privateModes[result5] = ch == 'h';
                            }
                        }
                    }
                    return true;
                }
            case 'p':
                if (items.Length == 1 && items[0] == "!")
                {
                    escapeTrace?.SetDescription("Reset");
                    Reset();
                }
                else
                {
                    escapeTrace?.SetDescription("Unknown 'p' cmd");
                }
                return true;
            case 's':
                escapeTrace?.SetDescription("Save features: " + string.Join(";", items));
                if (flag)
                {
                    string[] array = items;
                    for (int num2 = 0; num2 < array.Length; num2++)
                    {
                        if (int.TryParse(array[num2], out var result7) && result7 < _privateModes.Length)
                        {
                            _savedPrivateModes[result7] = _privateModes[result7];
                        }
                    }
                }
                return true;
            case 'r':
                if (flag)
                {
                    escapeTrace?.SetDescription("Restore features: " + string.Join(";", items));
                    string[] array = items;
                    for (int num2 = 0; num2 < array.Length; num2++)
                    {
                        if (int.TryParse(array[num2], out var result2) && result2 < _privateModes.Length)
                        {
                            SetMode(result2, flag: true, _savedPrivateModes[result2], escapeTrace);
                            _privateModes[result2] = _savedPrivateModes[result2];
                        }
                    }
                }
                else
                {
                    if (items.Length < 1 || !int.TryParse(items[0], out var result3))
                    {
                        result3 = 0;
                    }
                    if (items.Length < 2 || !int.TryParse(items[1], out var result4))
                    {
                        result4 = 0;
                    }
                    if (result3 == 0 || result4 == 0 || result4 < result3)
                    {
                        escapeTrace?.SetDescription("Reset clip region");
                        ClearScrollRegion();
                    }
                    else
                    {
                        escapeTrace?.SetDescription($"Set clip region to {result3}..{result4}");
                        SetScrollRegion(result3 - 1, result4 - 1);
                    }
                }
                return true;
            default:
                return false;
        }
    }

    private void SetMode(int value2, bool flag, bool flag2, EscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription($"Terminal feature: {CurrentStyle}");
        if (flag)
        {
            switch (value2)
            {
                case 1:
                    this._applicationCursorKeys = flag2;
                    break;
                case 25:
                    HideCaret = !flag2;
                    break;
                case 47:
                case 1049:
                    base.ActiveBufferIndex = (flag2 ? 1 : 0);
                    break;
                case 9:
                case 1000:
                case 1001:
                case 1002:
                case 1003:
                    if (flag2)
                    {
                        MouseMode = (MouseReportingMode)value2;
                    }
                    else
                    {
                        MouseMode = MouseReportingMode.Off;
                    }
                    break;
            }
        }
        else if (value2 == 4)
        {
            _insertMode = flag2;
        }
    }

    private void TraceFormat(string text, params object[] items)
    {
        Trace(string.Format(text, items));
    }

    private bool ExecuteOsc(string[] items, EscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription("OSC");
        if (items.Length < 2)
        {
            return false;
        }
        if (!int.TryParse(items[0], out var result))
        {
            return false;
        }
        if ((uint)result <= 2u)
        {
            base.Title = items[1];
            return true;
        }
        return false;
    }

    protected override void AddRef()
    {
        base.AddRef();
        if (TerminalSizeChanged == null)
        {
            return;
        }
        if (Dispatcher == null)
        {
            TerminalSizeChanged(this, _visibleColumns, ((ReadonlyTextBrowser)this)._visibleRows);
            return;
        }
        Dispatcher.PostResize(() =>
        {
            TerminalSizeChanged(this, _visibleColumns, ((ReadonlyTextBrowser)this)._visibleRows);
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!UsesTerminalInput)
        {
            base.OnKeyDown(e);
            return;
        }
        if (!e.Alt)
        {
            if (e.Shift && !e.Control && e.KeyCode == Keys.Insert)
            {
                Paste();
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            if (!e.Shift && e.Control && e.KeyCode == Keys.Insert)
            {
                if (AppSettings != null && AppSettings.ShowCtrlInsertWarnings && MessageBox.Show("The text is automatically copied to clipboard when you select it with your mouse. Ctrl-Insert will be now sent to remote computer.\nDo you want to keep being notified about this when you press Ctrl-Insert?", "Terminal", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No)
                {
                    AppSettings.ShowCtrlInsertWarnings = false;
                    AppSettings.Save();
                }
                CopySelection();
            }
            else if (AppSettings != null && AppSettings.ShiftPageXXToScroll && e.Shift && !e.Control && (e.KeyCode == Keys.Prior || e.KeyCode == Keys.Next))
            {
                if (e.KeyCode == Keys.Prior)
                {
                    TopLine -= ((ReadonlyTextBrowser)this)._visibleRows;
                }
                else
                {
                    TopLine += ((ReadonlyTextBrowser)this)._visibleRows;
                }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.F4 || !e.Alt || e.Control || e.Shift || ForwardAltF4)
        {
            SendKey(e);
        }
    }

    public void SendKey(KeyEventArgs e)
    {
        if (!_keyMappingEnabled || _keyMap == null)
        {
            return;
        }
        bool flag = _keyMap.IsNumPadKey(e.KeyCode, _extendedKey);
        string text = _keyMap.GetEscapeSequence(e.KeyCode, _applicationKeypad, this._applicationCursorKeys, flag);
        if (text != null)
        {
            text = text.Insert(0, "\u001b");
            int num = (e.Control ? 4 : 0) | (e.Alt ? 2 : 0) | (e.Shift ? 1 : 0);
            if (num != 0)
            {
                int num2 = num + 1;
                text = ((text.Length != 3 || text[0] != '\u001b' || text[1] != 'O') ? text.Insert(text.Length - 1, ";" + num2) : $"\u001b[1;{num2}{text[2]}");
            }
            SendText(text, InputKind.KeySequence);
            e.SuppressKeyPress = true;
        }
    }

    public bool SendAltChar(char ch)
    {
        if (!AppSettings.SendAltPlusLetterToSession)
        {
            return false;
        }
        SendText("\u001b" + ch, InputKind.KeySequence);
        return true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 256)
        {
            _extendedKey = ((int)message.LParam & 0x1000000) != 0;
            base.WndProc(ref message);
            _extendedKey = false;
        }
        else
        {
            base.WndProc(ref message);
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!UsesTerminalInput)
        {
            base.OnKeyPress(e);
        }
        else if (e.KeyChar == '\b' && !string.IsNullOrEmpty(BackspaceSequence))
        {
            SendText(BackspaceSequence, InputKind.Typed);
        }
        else if (e.KeyChar == '\r' && EnterSequence != null)
        {
            SendText(EnterSequence, InputKind.Typed);
        }
        else
        {
            SendText(new string(e.KeyChar, 1), InputKind.Typed);
        }
    }

    protected void SendText(string text, InputKind inputKind)
    {
        if (DataEntered == null)
        {
            return;
        }
        if (Dispatcher != null)
        {
            Dispatcher.PostInput(() =>
            {
                DataEntered(this, Encoding.UTF8.GetBytes(text), inputKind);
            });
        }
        else
        {
            DataEntered(this, Encoding.UTF8.GetBytes(text), inputKind);
        }
    }

    protected void SendBytes(byte[] buffer, InputKind inputKind)
    {
        if (DataEntered == null)
        {
            return;
        }
        if (Dispatcher != null)
        {
            Dispatcher.PostInput(() =>
            {
                DataEntered(this, buffer, inputKind);
            });
        }
        else
        {
            DataEntered(this, buffer, inputKind);
        }
    }

    private static byte EncodeMouseValue(int value2)
    {
        int num = value2 + 32;
        if (num >= 255)
        {
            return byte.MaxValue;
        }
        return (byte)num;
    }

    private void HandleMouseButton(MouseEventArgs e2, bool flag)
    {
        int num;
        if (!flag)
        {
            num = 3;
        }
        else
        {
            switch (e2.Button)
            {
                default:
                    return;
                case MouseButtons.Left:
                    num = 0;
                    break;
                case MouseButtons.Right:
                    num = 2;
                    break;
                case MouseButtons.Middle:
                    num = 1;
                    break;
            }
        }
        Point point = HitTestCell(e2.Location);
        switch (_mouseMode)
        {
            default:
                return;
            case MouseReportingMode.X10:
                if (!flag)
                {
                    return;
                }
                break;
            case MouseReportingMode.Normal:
            case MouseReportingMode.Highlight:
            case MouseReportingMode.ButtonEvent:
            case MouseReportingMode.AnyEvent:
                num |= MouseModifierBits;
                break;
        }
        SendMouseReport(num, point.X, point.Y);
    }

    private void HandleMouseWheel(MouseEventArgs e)
    {
        Point point = HitTestCell(e.Location);
        int num = 64 + ((e.Delta <= 0) ? 1 : 0);
        MouseReportingMode mouseReportingMode = _mouseMode;
        if (mouseReportingMode != MouseReportingMode.X10 && (uint)(mouseReportingMode - 1000) <= 3u)
        {
            num |= MouseModifierBits;
            SendMouseReport(num, point.X, point.Y);
        }
    }

    private void HandleMouseMove(MouseEventArgs e2)
    {
        int num = 0;
        switch (e2.Button)
        {
            case MouseButtons.Left:
                num = 0;
                break;
            case MouseButtons.Right:
                num = 2;
                break;
            case MouseButtons.Middle:
                num = 1;
                break;
        }
        switch (_mouseMode)
        {
            default:
                return;
            case MouseReportingMode.ButtonEvent:
                if (e2.Button == MouseButtons.None)
                {
                    return;
                }
                break;
            case MouseReportingMode.AnyEvent:
                break;
        }
        Point point = HitTestCell(e2.Location);
        if (!(point == _lastMouseCell))
        {
            _lastMouseCell = point;
            SendMouseReport(num + 32, point.X, point.Y);
        }
    }

    private void SendMouseReport(int value2, int value3, int value4)
    {
        SendBytes(new byte[6]
        {
            27,
            91,
            77,
            EncodeMouseValue(value2),
            EncodeMouseValue(value3 + 1),
            EncodeMouseValue(value4 + 1)
        }, InputKind.MouseReport);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location))
        {
            HandleMouseButton(e, flag: true);
            return;
        }
        if (e.Button == MouseButtons.Middle)
        {
            Paste();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location))
        {
            HandleMouseButton(e, flag: false);
        }
        else
        {
            base.OnMouseUp(e);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location))
        {
            HandleMouseMove(e);
        }
        else
        {
            base.OnMouseMove(e);
        }
    }

    public override void Paste()
    {
        try
        {
            if (!UsesTerminalInput)
            {
                base.Paste();
                return;
            }
            string text = Win32Clipboard.GetText(Handle);
            if (text != null)
            {
                text = text.Replace("\r", "");
            }
            SendText(text, InputKind.Paste);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void EnqueueOutput(string text)
    {
        lock (_outputLock)
        {
            _pendingOutput += text;
        }
        if (Interlocked.Increment(ref _pendingFlushes) >= 8)
        {
            return;
        }
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new ThreadStart(FlushOutput));
            }
        }
        catch
        {
        }
    }

    private void FlushOutput()
    {
        string text;
        lock (_outputLock)
        {
            text = _pendingOutput;
            _pendingOutput = null;
        }
        if (text != null)
        {
            ProcessOutput(text);
        }
    }

    private void OnFlushTick(object sender, EventArgs e)
    {
        _pendingFlushes = 0;
        FlushOutput();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location))
        {
            HandleMouseWheel(e);
        }
        else if ((Control.ModifierKeys & Keys.Alt) != Keys.Alt && AppSettings != null && AppSettings.EmulateDirectionKeysWithMouseWheel)
        {
            string text = _keyMap.GetEscapeSequence((e.Delta < 0) ? Keys.Down : Keys.Up, _applicationKeypad, this._applicationCursorKeys, flag3: false);
            if (text != null)
            {
                text = text.Insert(0, "\u001b");
                SendText(text, InputKind.KeySequence);
            }
        }
        else
        {
            base.OnMouseWheel(e);
        }
    }

    public void OpenLogFile(string text)
    {
        if (_logStream != null)
        {
            _logStream.Dispose();
            _logStream = null;
        }
        if (text != null)
        {
            _logStream = File.Open(text, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        }
    }

    public void WriteLog(byte[] buffer, int value2, int value3)
    {
        try
        {
            FileStream fileStream = _logStream;
            if (fileStream != null)
            {
                fileStream.Write(buffer, value2, value3);
                fileStream.Flush();
            }
        }
        catch
        {
        }
    }

    public override void BeginNewSession()
    {
        base.BeginNewSession();
        MouseMode = MouseReportingMode.Off;
    }

    public override void Reset()
    {
        base.Reset();
        MouseMode = MouseReportingMode.Off;
        lock (_outputLock)
        {
            _pendingOutput = null;
        }
        _incompleteEscape = null;
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (CaptureDialogKeys)
        {
            KeyEventArgs keyEventArgs = new KeyEventArgs(keyData);
            if (!keyEventArgs.Control && !keyEventArgs.Shift && !keyEventArgs.Alt && keyEventArgs.KeyCode == Keys.Escape)
            {
                SendBytes(new byte[1] { 27 }, InputKind.KeySequence);
            }
            else if (keyEventArgs.Control && !keyEventArgs.Alt && !keyEventArgs.Shift && keyEventArgs.KeyCode >= Keys.A && keyEventArgs.KeyCode <= Keys.Z)
            {
                SendBytes(new byte[1] { (byte)(1 + (keyEventArgs.KeyCode - 65)) }, InputKind.KeySequence);
            }
            else
            {
                if (keyEventArgs.KeyCode < Keys.F1 || keyEventArgs.KeyCode > Keys.F12)
                {
                    return base.ProcessDialogKey(keyData);
                }
                OnKeyDown(keyEventArgs);
            }
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}
