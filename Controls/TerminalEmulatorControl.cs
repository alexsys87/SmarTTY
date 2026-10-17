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

        internal void SetInput(string text, int position)
        {
            _input = text;
            _position = position;
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

    public delegate void TerminalSizeChangedHandler(TerminalEmulatorControl terminalEmulatorControl, int columns, int rows);

    public enum InputKind
    {
        Typed,
        KeySequence,
        MouseReport,
        Paste
    }

    public delegate void TerminalDataHandler(object sender, byte[] buffer, InputKind inputKind);

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
            int modifiers = 0;
            if ((Control.ModifierKeys & Keys.Shift) != 0)
            {
                modifiers |= 4;
            }
            if ((Control.ModifierKeys & Keys.Alt) != 0)
            {
                modifiers |= 8;
            }
            if ((Control.ModifierKeys & Keys.Control) != 0)
            {
                modifiers |= 0x10;
            }
            return modifiers;
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

    public void ProcessOutput(string output)
    {
        if (_incompleteEscape != null)
        {
            output = _incompleteEscape + output;
            _incompleteEscape = null;
        }
        if (output == null)
        {
            return;
        }
        if (MaxChunkLength != 0 && output.Length > MaxChunkLength)
        {
            output = $"\r\n--- skipped {output.Length - MaxChunkLength} bytes ---\r\n" + output.Substring(output.Length - MaxChunkLength);
            GC.Collect();
        }
        int segmentStart = 0;
        try
        {
            BeginBatch();
            for (int charIndex = 0; charIndex < output.Length; charIndex++)
            {
                if (output[charIndex] >= ' ')
                {
                    continue;
                }
                WriteText(output.Substring(segmentStart, charIndex - segmentStart));
                if (output[charIndex] != '\u001b')
                {
                    base.ResetLinkTracking();
                }
                segmentStart = charIndex + 1;
                EscapeTrace escapeTrace = null;
                switch (output[charIndex])
                {
                    case '\r':
                        Trace("<CR>");
                        MoveCursorBy(-base.CursorPosition.X, 0);
                        break;
                    case '\n':
                        Trace("<LF>");
                        MoveCursorBy(0, 1, scroll: true);
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
                            int consumed = ParseEscapeSequence(output, charIndex, out var text, escapeTrace);
                            charIndex += consumed - 1;
                            segmentStart += consumed - 1;
                            if (text != null)
                            {
                                WriteText(text);
                            }
                            break;
                        }
                    default:
                        if (ShowUnrecognizedAsHex)
                        {
                            WriteText($"\\{(int)output[charIndex]}");
                        }
                        break;
                }
            }
            if (segmentStart < output.Length)
            {
                WriteText(output.Substring(segmentStart, output.Length - segmentStart));
            }
        }
        finally
        {
            EndBatch();
        }
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(string text, UIntPtr handle, uint colorIndex);

    public static string ForegroundCode(int colorIndex)
    {
        return $"{'\u001b'}[{30 + colorIndex}m";
    }

    public static string Colorize(string text, int colorIndex)
    {
        return ForegroundCode(colorIndex) + text + ResetSequence;
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

    private static int[] ParseParameters(string[] items, params int[] defaults)
    {
        int[] parameters = new int[Math.Max(items.Length, defaults.Length)];
        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            if (parameterIndex >= items.Length || !int.TryParse(items[parameterIndex], out parameters[parameterIndex]))
            {
                parameters[parameterIndex] = defaults[parameterIndex];
            }
        }
        return parameters;
    }

    private int ParseEscapeSequence(string text, int position, out string parsedText, EscapeTrace escapeTrace = null)
    {
        parsedText = null;
        escapeTrace?.SetInput(text, position);
        if (position + 1 >= text.Length)
        {
            _incompleteEscape = text.Substring(position);
            return 1;
        }
        switch (text[position + 1])
        {
            case ']':
                {
                    int oscEnd = AnsiEscapeUtils.FindOscEnd(text, position + 1, out var belEnd);
                    if (oscEnd == -1)
                    {
                        escapeTrace?.SetWarning("Invalid ST/BEL");
                        _incompleteEscape = text.Substring(position);
                        return _incompleteEscape.Length;
                    }
                    ExecuteOsc(text.Substring(position + 2, oscEnd - position - 2).Split(';'), escapeTrace);
                    return belEnd + 1;
                }
            case '[':
                {
                    int csiEnd = AnsiEscapeUtils.FindCsiEnd(text, position + 2);
                    if (csiEnd == -1)
                    {
                        escapeTrace?.SetWarning("Invalid CSI");
                        _incompleteEscape = text.Substring(position);
                        return _incompleteEscape.Length;
                    }
                    int parameterStart = 0;
                    if (text[position + 2] == '?')
                    {
                        parameterStart = 1;
                    }
                    ExecuteCsi(text[csiEnd], text.Substring(position + 2 + parameterStart, csiEnd - position - 2 - parameterStart).Split(';'), parameterStart != 0, escapeTrace);
                    return csiEnd - position + 1;
                }
            case ' ':
            case '#':
            case '%':
            case '(':
            case ')':
            case '*':
            case '+':
                if (text.Length - position < 3)
                {
                    _incompleteEscape = text.Substring(position);
                    return _incompleteEscape.Length;
                }
                escapeTrace?.SetRaw("[ESC]" + text.Substring(position, 2));
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
                    MoveCursorBy(0, 1, scroll: true);
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
                    MoveCursorBy(0, -1, scroll: true);
                }
                return 2;
            case 'E':
                escapeTrace?.SetDescription("Got to next line");
                MoveCursorBy(-base.CursorPosition.X, 1, scroll: true);
                return 2;
            default:
                return 2;
        }
    }

    internal bool ExecuteCsi(char ch, string[] items, bool set, EscapeTrace escapeTrace = null)
    {
        escapeTrace?.SetRaw(string.Format("Unknown CSI command: {2}{0}, DEC={1}", ch, set, string.Join(";", items)));
        int count = 0;
        int repeatCount = 1;
        if (items.Length != 0 && int.TryParse(items[0], out count))
        {
            repeatCount = count;
        }
        switch (ch)
        {
            case '@':
                InsertChars(repeatCount);
                escapeTrace?.SetDescription($"Insert char at {repeatCount}");
                return true;
            case 'A':
                escapeTrace?.SetDescription($"Go up {repeatCount}");
                MoveCursorBy(0, -repeatCount);
                return true;
            case 'B':
                escapeTrace?.SetDescription($"Go down {repeatCount}");
                MoveCursorBy(0, repeatCount);
                return true;
            case 'C':
                escapeTrace?.SetDescription($"Go left {repeatCount}");
                MoveCursorBy(repeatCount, 0);
                return true;
            case 'D':
                escapeTrace?.SetDescription($"Go right {repeatCount}");
                MoveCursorBy(-repeatCount, 0);
                return true;
            case 'E':
                escapeTrace?.SetDescription($"Go to start of relative line {repeatCount}");
                MoveCursorBy(-base.CursorPosition.X, repeatCount);
                return true;
            case 'F':
                escapeTrace?.SetDescription($"Go to start of relative line -{repeatCount}");
                MoveCursorBy(-base.CursorPosition.X, -repeatCount);
                return true;
            case 'G':
            case '`':
                escapeTrace?.SetDescription($"Set X= {repeatCount}");
                base.CursorPosition = new Point(repeatCount - 1, base.CursorPosition.Y);
                return true;
            case 'H':
            case 'f':
                {
                    int[] coordinates = ParseParameters(items, 1, 1);
                    escapeTrace?.SetDescription($"GOTOXY {coordinates[1]}, {coordinates[0]}");
                    base.CursorPosition = new Point(coordinates[1] - 1, coordinates[0] - 1);
                    return true;
                }
            case 'd':
                escapeTrace?.SetDescription($"Set Y= {repeatCount}");
                base.CursorPosition = new Point(base.CursorPosition.X, repeatCount - 1);
                return true;
            case 'I':
                {
                    escapeTrace?.SetDescription($"Go {repeatCount} tabs");
                    for (int tabIndex = 0; tabIndex < repeatCount; tabIndex++)
                    {
                        MoveCursorBy(Tabs.NextDistance(base.CursorPosition.X, _tabWidth), 0);
                    }
                    return true;
                }
            case 'J':
                escapeTrace?.SetDescription($"Erase terminal {(EraseMode)count}");
                if ((uint)count <= 2u)
                {
                    EraseInDisplay((EraseMode)count);
                }
                InvalidateView();
                return true;
            case 'K':
                escapeTrace?.SetDescription($"Erase line {(EraseMode)count}");
                if ((uint)count <= 2u)
                {
                    EraseInLine((EraseMode)count);
                }
                InvalidateView();
                return true;
            case 'L':
                escapeTrace?.SetDescription($"Insert {repeatCount} lines");
                InsertBlankLines(base.CursorPosition.Y, repeatCount);
                return true;
            case 'M':
                escapeTrace?.SetDescription($"Remove {repeatCount} lines");
                ScrollRegionUp(base.CursorPosition.Y, repeatCount);
                return true;
            case 'P':
                escapeTrace?.SetDescription($"Erase {repeatCount} chars");
                DeleteChars(repeatCount);
                return true;
            case 'S':
                escapeTrace?.SetDescription($"Remove {repeatCount} lines from clip region top");
                ScrollRegionUp(base.ScrollTop, repeatCount);
                return true;
            case 'T':
                escapeTrace?.SetDescription($"Insert {repeatCount} lines to clip region top");
                InsertBlankLines(base.ScrollTop, repeatCount);
                return true;
            case 'X':
                escapeTrace?.SetDescription($"Pad {repeatCount} chars");
                WriteText(new string(' ', repeatCount), moveCursor: false);
                return true;
            case 'Z':
                {
                    escapeTrace?.SetDescription($"Go {repeatCount} tabs back");
                    for (int backTabIndex = 0; backTabIndex < repeatCount; backTabIndex++)
                    {
                        MoveCursorBy(-Tabs.PreviousDistance(base.CursorPosition.X), 0);
                    }
                    return true;
                }
            case 'b':
                escapeTrace?.SetDescription("Clone char");
                RepeatPreviousChar(repeatCount);
                return true;
            case 'g':
                escapeTrace?.SetDescription("Clear/reset tab");
                switch (count)
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
                    string[] parameters = items;
                    for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                    {
                        int.TryParse(parameters[parameterIndex], out var parameter);
                        if (parameter >= 30 && parameter <= 37)
                        {
                            CurrentStyle.Foreground = (byte)(1 + parameter - 30);
                            continue;
                        }
                        if (parameter >= 40 && parameter <= 47)
                        {
                            CurrentStyle.Background = (byte)(1 + parameter - 40);
                            continue;
                        }
                        switch (parameter)
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
                                CurrentStyle.Foreground = (byte)(1 + parameter - 90);
                                CurrentStyle.Bold = true;
                                continue;
                        }
                        if (parameter >= 100 && parameter <= 107)
                        {
                            CurrentStyle.Background = (byte)(1 + parameter - 100);
                            CurrentStyle.Bold = true;
                        }
                    }
                    escapeTrace?.SetDescription($"SGR: {CurrentStyle}");
                    return true;
                }
            case 'h':
            case 'l':
                {
                    string[] sgrParameters = items;
                    for (int sgrParameterIndex = 0; sgrParameterIndex < sgrParameters.Length; sgrParameterIndex++)
                    {
                        if (int.TryParse(sgrParameters[sgrParameterIndex], out var mode))
                        {
                            SetMode(mode, set, ch == 'h', escapeTrace);
                            if (set && mode < _privateModes.Length)
                            {
                                _privateModes[mode] = ch == 'h';
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
                if (set)
                {
                    string[] modeParameters = items;
                    for (int modeParameterIndex = 0; modeParameterIndex < modeParameters.Length; modeParameterIndex++)
                    {
                        if (int.TryParse(modeParameters[modeParameterIndex], out var modeValue) && modeValue < _privateModes.Length)
                        {
                            _savedPrivateModes[modeValue] = _privateModes[modeValue];
                        }
                    }
                }
                return true;
            case 'r':
                if (set)
                {
                    escapeTrace?.SetDescription("Restore features: " + string.Join(";", items));
                    string[] savedModeParameters = items;
                    for (int savedModeParameterIndex = 0; savedModeParameterIndex < savedModeParameters.Length; savedModeParameterIndex++)
                    {
                        if (int.TryParse(savedModeParameters[savedModeParameterIndex], out var savedModeValue) && savedModeValue < _privateModes.Length)
                        {
                            SetMode(savedModeValue, set: true, _savedPrivateModes[savedModeValue], escapeTrace);
                            _privateModes[savedModeValue] = _savedPrivateModes[savedModeValue];
                        }
                    }
                }
                else
                {
                    if (items.Length < 1 || !int.TryParse(items[0], out var top))
                    {
                        top = 0;
                    }
                    if (items.Length < 2 || !int.TryParse(items[1], out var bottom))
                    {
                        bottom = 0;
                    }
                    if (top == 0 || bottom == 0 || bottom < top)
                    {
                        escapeTrace?.SetDescription("Reset clip region");
                        ClearScrollRegion();
                    }
                    else
                    {
                        escapeTrace?.SetDescription($"Set clip region to {top}..{bottom}");
                        SetScrollRegion(top - 1, bottom - 1);
                    }
                }
                return true;
            default:
                return false;
        }
    }

    private void SetMode(int mode, bool set, bool enabled, EscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription($"Terminal feature: {CurrentStyle}");
        if (set)
        {
            switch (mode)
            {
                case 1:
                    this._applicationCursorKeys = enabled;
                    break;
                case 25:
                    HideCaret = !enabled;
                    break;
                case 47:
                case 1049:
                    base.ActiveBufferIndex = (enabled ? 1 : 0);
                    break;
                case 9:
                case 1000:
                case 1001:
                case 1002:
                case 1003:
                    if (enabled)
                    {
                        MouseMode = (MouseReportingMode)mode;
                    }
                    else
                    {
                        MouseMode = MouseReportingMode.Off;
                    }
                    break;
            }
        }
        else if (mode == 4)
        {
            _insertMode = enabled;
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
        if (!int.TryParse(items[0], out var eraseCode))
        {
            return false;
        }
        if ((uint)eraseCode <= 2u)
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
        bool numPad = _keyMap.IsNumPadKey(e.KeyCode, _extendedKey);
        string text = _keyMap.GetEscapeSequence(e.KeyCode, _applicationKeypad, this._applicationCursorKeys, numPad);
        if (text != null)
        {
            text = text.Insert(0, "\u001b");
            int modifiers = (e.Control ? 4 : 0) | (e.Alt ? 2 : 0) | (e.Shift ? 1 : 0);
            if (modifiers != 0)
            {
                int modifierCode = modifiers + 1;
                text = ((text.Length != 3 || text[0] != '\u001b' || text[1] != 'O') ? text.Insert(text.Length - 1, ";" + modifierCode) : $"\u001b[1;{modifierCode}{text[2]}");
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

    private static byte EncodeMouseValue(int mouseCode)
    {
        int encoded = mouseCode + 32;
        if (encoded >= 255)
        {
            return byte.MaxValue;
        }
        return (byte)encoded;
    }

    private void HandleMouseButton(MouseEventArgs e, bool release)
    {
        int buttonCode;
        if (!release)
        {
            buttonCode = 3;
        }
        else
        {
            switch (e.Button)
            {
                default:
                    return;
                case MouseButtons.Left:
                    buttonCode = 0;
                    break;
                case MouseButtons.Right:
                    buttonCode = 2;
                    break;
                case MouseButtons.Middle:
                    buttonCode = 1;
                    break;
            }
        }
        Point point = HitTestCell(e.Location);
        switch (_mouseMode)
        {
            default:
                return;
            case MouseReportingMode.X10:
                if (!release)
                {
                    return;
                }
                break;
            case MouseReportingMode.Normal:
            case MouseReportingMode.Highlight:
            case MouseReportingMode.ButtonEvent:
            case MouseReportingMode.AnyEvent:
                buttonCode |= MouseModifierBits;
                break;
        }
        SendMouseReport(buttonCode, point.X, point.Y);
    }

    private void HandleMouseWheel(MouseEventArgs e)
    {
        Point point = HitTestCell(e.Location);
        int wheelCode = 64 + ((e.Delta <= 0) ? 1 : 0);
        MouseReportingMode mouseReportingMode = _mouseMode;
        if (mouseReportingMode != MouseReportingMode.X10 && (uint)(mouseReportingMode - 1000) <= 3u)
        {
            wheelCode |= MouseModifierBits;
            SendMouseReport(wheelCode, point.X, point.Y);
        }
    }

    private void HandleMouseMove(MouseEventArgs e)
    {
        int buttonCode = 0;
        switch (e.Button)
        {
            case MouseButtons.Left:
                buttonCode = 0;
                break;
            case MouseButtons.Right:
                buttonCode = 2;
                break;
            case MouseButtons.Middle:
                buttonCode = 1;
                break;
        }
        switch (_mouseMode)
        {
            default:
                return;
            case MouseReportingMode.ButtonEvent:
                if (e.Button == MouseButtons.None)
                {
                    return;
                }
                break;
            case MouseReportingMode.AnyEvent:
                break;
        }
        Point point = HitTestCell(e.Location);
        if (!(point == _lastMouseCell))
        {
            _lastMouseCell = point;
            SendMouseReport(buttonCode + 32, point.X, point.Y);
        }
    }

    private void SendMouseReport(int buttonCode, int x, int y)
    {
        SendBytes(new byte[6]
        {
            27,
            91,
            77,
            EncodeMouseValue(buttonCode),
            EncodeMouseValue(x + 1),
            EncodeMouseValue(y + 1)
        }, InputKind.MouseReport);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location))
        {
            HandleMouseButton(e, release: true);
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
            HandleMouseButton(e, release: false);
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
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
            string text = _keyMap.GetEscapeSequence((e.Delta < 0) ? Keys.Down : Keys.Up, _applicationKeypad, this._applicationCursorKeys, isNumPadKey: false);
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

    public void WriteLog(byte[] buffer, int offset, int count)
    {
        try
        {
            FileStream fileStream = _logStream;
            if (fileStream != null)
            {
                fileStream.Write(buffer, offset, count);
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
