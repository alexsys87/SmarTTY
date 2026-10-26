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

    // An escape sequence (an OSC title, say) that has not ended after this many characters is dropped, so that a
    // stray ESC ] in binary output cannot swallow everything that follows while waiting for its terminator.
    private const int MaxPendingEscapeLength = 16384;

    private const int BracketedPasteMode = 2004;

    private const int SgrMouseMode = 1006;

    private Point _savedCursor;

    private CellStyle _savedStyle;

    // Set while Reset() runs for an ESC c received from the remote side: the output queued after it is still valid.
    private bool _resetFromStream;

    private bool _byteOrientedLineEditing;

    // Byte mode (see ByteOrientedLineEditing): the remote cursor is this many bytes into the character at the cursor.
    private int _byteOffset;

    // Byte mode: a cell shown as U+FFFD that holds the first bytes of a letter the remote editor printed one byte at
    // a time (document line, column and the bytes received so far); the next bytes complete it in place.
    private int _partialLine = -1;

    private int _partialColumn = -1;

    private System.Collections.Generic.List<byte> _partialBytes;

    private readonly DecCharsets _charsets = new DecCharsets();

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

    private StringBuilder _pendingOutput = new StringBuilder();

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

    /// <summary>
    /// The remote line editor counts UTF-8 bytes rather than characters (BusyBox ash without Unicode support, as on
    /// OpenWrt). It then moves its cursor one column per byte, so a Backspace after a Cyrillic letter (two bytes) used
    /// to erase the letter from the screen but only half of it from the command line, and every further Backspace
    /// erased one more letter from the screen than from the line. In this mode, on the normal screen, the terminal
    /// converts the editor's cursor moves from bytes into screen cells and repeats Backspace, Delete, Left and Right
    /// once per byte of the letter they cross (see <see cref="ByteColumns"/>).
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ByteOrientedLineEditing
    {
        get
        {
            return _byteOrientedLineEditing;
        }
        set
        {
            _byteOrientedLineEditing = value;
            ResetByteState();
        }
    }

    private bool ByteColumnsActive => _byteOrientedLineEditing && base.ActiveBufferIndex == 0 && !_processingLocalOutput;

    // Set while text the terminal draws on its own (the local echo of a COM/TCP connection) is processed: it is
    // measured in characters, not in the remote editor's bytes.
    private bool _processingLocalOutput;

    /// <summary>
    /// Draws text that the terminal produces itself (local echo) rather than the remote side: in byte mode
    /// (<see cref="ByteOrientedLineEditing"/>) its "\b" steps back one character, not one byte.
    /// </summary>
    protected void ProcessLocalOutput(string output)
    {
        _processingLocalOutput = true;
        try
        {
            ProcessOutput(output);
        }
        finally
        {
            _processingLocalOutput = false;
        }
    }

    /// <summary>Forgets a half-crossed character of byte mode, e.g. when a new command starts in Smart Terminal.</summary>
    protected void ResetByteColumns()
    {
        ResetByteState();
    }

    /// <summary>
    /// What a line break of pasted text is sent as while the remote side reads the keyboard (<see cref="UsesTerminalInput"/>).
    /// SSH terminals send CR, like the Enter key and other terminal emulators; the default LF keeps the behaviour
    /// the COM/TCP terminal has always had.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PasteLineBreak { get; set; } = "\n";

    /// <summary>
    /// Suppresses the answers to terminal queries (cursor position, device attributes) while output that is not
    /// live, such as a replayed log, is processed: those answers would otherwise be typed into the live session.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SuppressReplies { get; set; }

    /// <summary>
    /// Encoding used to turn typed and pasted text into bytes. SSH sessions keep the UTF-8 default;
    /// the COM/TCP terminal sets it from its settings so that input matches how received data is decoded.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Encoding InputEncoding { get; set; } = Encoding.UTF8;

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
                WriteOutputText(output.Substring(segmentStart, charIndex - segmentStart));
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
                        _byteOffset = 0;
                        MoveCursorBy(-base.CursorPosition.X, 0);
                        break;
                    case '\n':
                        Trace("<LF>");
                        _byteOffset = 0;
                        MoveCursorBy(0, 1, scroll: true);
                        break;
                    case '\t':
                        Trace("<TAB>");
                        _byteOffset = 0;
                        MoveCursorBy(Tabs.NextDistance(base.CursorPosition.X, _tabWidth), 0);
                        break;
                    case '\a':
                        Trace("<BEL>");
                        OnBell();
                        break;
                    case '\u000e':
                        Trace("<SO>");
                        _charsets.ShiftOut();
                        break;
                    case '\u000f':
                        Trace("<SI>");
                        _charsets.ShiftIn();
                        break;
                    case '\b':
                        Trace("<BACK>");
                        if (ByteColumnsActive && MoveBackBytes(1) == 0)
                        {
                            break;
                        }
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
                WriteOutputText(output.Substring(segmentStart, output.Length - segmentStart));
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
        if (text[position + 1] != '[' && text[position + 1] != ']')
        {
            // ESC 7/8, ESC D/E/M, ESC c and the like place the cursor on a cell boundary.
            _byteOffset = 0;
        }
        switch (text[position + 1])
        {
            case ']':
                {
                    int consumed = ParseControlString(text, position, out string content, escapeTrace);
                    if (content != null)
                    {
                        ExecuteOsc(content, escapeTrace);
                    }
                    return consumed;
                }
            case 'P':
            case 'X':
            case '^':
            case '_':
                // DCS, SOS, PM and APC strings are not supported, but their contents must not be shown as text.
                escapeTrace?.SetDescription("Ignored control string");
                return ParseControlString(text, position, out _, escapeTrace);
            case '[':
                {
                    int csiEnd = AnsiEscapeUtils.FindCsiEnd(text, position + 2);
                    if (csiEnd == -1)
                    {
                        escapeTrace?.SetWarning("Invalid CSI");
                        return KeepIncompleteEscape(text, position, escapeTrace);
                    }
                    ExecuteCsiSequence(text[csiEnd], text.Substring(position + 2, csiEnd - position - 2), escapeTrace);
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
                if (text[position + 1] == '(' || text[position + 1] == ')')
                {
                    // Character set designation: ESC ( 0 = DEC Special Graphics (line drawing) for G0, ESC ( B = ASCII.
                    _charsets.Designate(text[position + 1], text[position + 2]);
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
                SaveCursor();
                return 2;
            case '8':
                escapeTrace?.SetDescription("Restore cursor");
                RestoreCursor();
                return 2;
            case 'H':
                escapeTrace?.SetDescription("Set tab");
                Tabs.SetStop(base.CursorPosition.X);
                return 2;
            case 'c':
                escapeTrace?.SetDescription("Reset");
                ResetFromStream();
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

    /// <summary>
    /// Keeps an escape sequence that has not ended yet for the next chunk of output, or drops its ESC once it has
    /// grown beyond <see cref="MaxPendingEscapeLength"/>, so the rest is shown as text instead of being swallowed.
    /// Returns the number of characters consumed.
    /// </summary>
    private int KeepIncompleteEscape(string text, int position, EscapeTrace escapeTrace)
    {
        if (text.Length - position > MaxPendingEscapeLength)
        {
            escapeTrace?.SetWarning("Unterminated escape sequence dropped");
            return 1;
        }
        _incompleteEscape = text.Substring(position);
        return _incompleteEscape.Length;
    }

    /// <summary>
    /// Parses an OSC/DCS-style string (ESC x ... BEL or ESC x ... ESC \). As in xterm, CAN or SUB cancel the string
    /// and an ESC that does not start the terminator aborts it and begins the next sequence.
    /// <paramref name="content"/> receives the text between the introducer and the terminator, or null when the
    /// string was cancelled or is not complete yet. Returns the number of characters consumed.
    /// </summary>
    private int ParseControlString(string text, int position, out string content, EscapeTrace escapeTrace)
    {
        content = null;
        for (int index = position + 2; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch == '\a')
            {
                content = text.Substring(position + 2, index - position - 2);
                return index - position + 1;
            }
            if (ch == '\u0018' || ch == '\u001a')
            {
                return index - position + 1;
            }
            if (ch == '\u001b')
            {
                if (index + 1 >= text.Length)
                {
                    break;
                }
                if (text[index + 1] == '\\')
                {
                    content = text.Substring(position + 2, index - position - 2);
                    return index - position + 2;
                }
                escapeTrace?.SetWarning("Control string aborted by ESC");
                return index - position;
            }
        }
        escapeTrace?.SetWarning("Invalid ST/BEL");
        return KeepIncompleteEscape(text, position, escapeTrace);
    }

    /// <summary>
    /// Splits a CSI sequence into its private prefix (? > = <), parameters and intermediate bytes before running it.
    /// Sequences with a prefix other than '?' or with intermediates are xterm extensions that mostly are not
    /// supported; they must not be run as the standard sequence with the same final character
    /// (vim's "CSI > 4 ; 2 m" is not an SGR reset, "CSI 2 SP q" is not a scroll-region command).
    /// </summary>
    private void ExecuteCsiSequence(char final, string body, EscapeTrace escapeTrace)
    {
        char prefix = '\0';
        if (body.Length > 0 && body[0] >= '<' && body[0] <= '?')
        {
            prefix = body[0];
            body = body.Substring(1);
        }
        int intermediateStart = body.Length;
        while (intermediateStart > 0 && body[intermediateStart - 1] >= ' ' && body[intermediateStart - 1] <= '/')
        {
            intermediateStart--;
        }
        string intermediates = body.Substring(intermediateStart);
        body = body.Substring(0, intermediateStart);
        if (intermediates.Length != 0)
        {
            if (prefix == '\0' && intermediates == "!" && final == 'p')
            {
                escapeTrace?.SetDescription("Soft reset");
                SoftReset();
            }
            return;
        }
        if (prefix != '\0' && prefix != '?')
        {
            escapeTrace?.SetDescription($"Ignored private CSI {prefix}{body}{final}");
            return;
        }
        ExecuteCsi(final, body.Split(';'), prefix == '?', escapeTrace);
    }

    internal bool ExecuteCsi(char ch, string[] items, bool set, EscapeTrace escapeTrace = null)
    {
        if ("CDJKmXP@hlnc".IndexOf(ch) == -1)
        {
            // Every other command that moves the cursor puts it on a cell boundary (byte mode).
            _byteOffset = 0;
        }
        escapeTrace?.SetRaw(string.Format("Unknown CSI command: {2}{0}, DEC={1}", ch, set, string.Join(";", items)));
        int count = 0;
        int repeatCount = 1;
        if (items.Length != 0 && int.TryParse(items[0], out count) && count > 0)
        {
            // A count of 0 means 1, as an omitted one does.
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
                escapeTrace?.SetDescription($"Go right {repeatCount}");
                if (ByteColumnsActive)
                {
                    MoveForwardBytes(repeatCount);
                }
                else
                {
                    MoveCursorBy(repeatCount, 0);
                }
                return true;
            case 'D':
                escapeTrace?.SetDescription($"Go left {repeatCount}");
                if (ByteColumnsActive)
                {
                    MoveBackBytes(repeatCount);
                }
                else
                {
                    MoveCursorBy(-repeatCount, 0);
                }
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
                {
                    // ECH erases up to the end of the line, it never wraps onto the next one.
                    int eraseCount = Math.Min(repeatCount, _visibleColumns - base.CursorPosition.X);
                    escapeTrace?.SetDescription($"Pad {eraseCount} chars");
                    if (eraseCount > 0)
                    {
                        WriteAt(base.CursorPosition, new string(' ', eraseCount), CurrentStyle);
                    }
                    return true;
                }
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
                if (!set)
                {
                    ApplySgr(XtermProtocol.ParseSgr(items));
                    escapeTrace?.SetDescription($"SGR: {CurrentStyle}");
                }
                return true;
            case 'n':
                if (!set)
                {
                    ReportStatus(count, escapeTrace);
                }
                return true;
            case 'c':
                if (!set && count == 0)
                {
                    // Primary device attributes: a VT100 with advanced video option, as xterm reports by default.
                    escapeTrace?.SetDescription("Device attributes");
                    SendReply("\u001b[?1;2c");
                }
                return true;
            case 'u':
                if (!set)
                {
                    escapeTrace?.SetDescription("Restore cursor (SCORC)");
                    RestoreCursor();
                }
                return true;
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
            case 's':
                escapeTrace?.SetDescription("Save features: " + string.Join(";", items));
                if (!set)
                {
                    // SCOSC: the ANSI.SYS form of "save cursor".
                    SaveCursor();
                }
                else
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
                    // DECSTBM: an omitted (or 0) top is the first line, an omitted bottom the last one.
                    int rows = ((ReadonlyTextBrowser)this)._visibleRows;
                    if (items.Length < 1 || !int.TryParse(items[0], out var top) || top < 1)
                    {
                        top = 1;
                    }
                    if (items.Length < 2 || !int.TryParse(items[1], out var bottom) || bottom < 1 || bottom > rows)
                    {
                        bottom = rows;
                    }
                    if (top == 1 && bottom == rows)
                    {
                        escapeTrace?.SetDescription("Reset clip region");
                        ClearScrollRegion();
                    }
                    else if (top < bottom)
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
                    base.ActiveBufferIndex = (enabled ? 1 : 0);
                    break;
                case 1047:
                    // Alternate screen, cleared when it is left.
                    if (!enabled && base.ActiveBufferIndex == 1)
                    {
                        ClearActiveScreen();
                    }
                    base.ActiveBufferIndex = (enabled ? 1 : 0);
                    break;
                case 1048:
                    if (enabled)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }
                    break;
                case 1049:
                    // Alternate screen, cleared when it is entered. The cursor starts where it was on the normal
                    // screen, and the normal screen gets its own cursor back when the program leaves (each buffer
                    // keeps its cursor, so an ESC 7 issued by the program cannot move it).
                    if (enabled)
                    {
                        Point cursor = base.CursorPosition;
                        SaveCursor();
                        if (base.ActiveBufferIndex != 1)
                        {
                            base.ActiveBufferIndex = 1;
                        }
                        ClearActiveScreen();
                        base.CursorPosition = cursor;
                    }
                    else if (base.ActiveBufferIndex != 0)
                    {
                        base.ActiveBufferIndex = 0;
                    }
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

    private bool ExecuteOsc(string content, EscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription("OSC");
        // Only the first ';' separates the command from its text: a title may contain ';' itself.
        int separator = content.IndexOf(';');
        if (separator == -1 || !int.TryParse(content.Substring(0, separator), out var command))
        {
            return false;
        }
        // 0 sets the icon name and the title, 2 the title only; 1 (the icon name alone) does not touch the title.
        if (command == 0 || command == 2)
        {
            StringBuilder title = new StringBuilder();
            foreach (char ch in content.Substring(separator + 1))
            {
                if (!char.IsControl(ch))
                {
                    title.Append(ch);
                }
            }
            base.Title = title.ToString();
            return true;
        }
        return false;
    }

    private void ApplySgr(System.Collections.Generic.List<XtermProtocol.SgrAttribute> attributes)
    {
        foreach (XtermProtocol.SgrAttribute attribute in attributes)
        {
            int code = attribute.Code;
            if (code >= 30 && code <= 37)
            {
                CurrentStyle.Foreground = (byte)(1 + code - 30);
                CurrentStyle.ExtendedForeground = 0;
            }
            else if (code >= 40 && code <= 47)
            {
                CurrentStyle.Background = (byte)(1 + code - 40);
                CurrentStyle.ExtendedBackground = 0;
            }
            else if (code >= 90 && code <= 97)
            {
                // Bright colours are palette entries 8..15; they no longer turn bold on, which used to stick
                // to the text written after a later normal colour.
                CurrentStyle.Foreground = 0;
                CurrentStyle.ExtendedForeground = EncodeExtendedColor(8 + code - 90);
            }
            else if (code >= 100 && code <= 107)
            {
                CurrentStyle.Background = 0;
                CurrentStyle.ExtendedBackground = EncodeExtendedColor(8 + code - 100);
            }
            else
            {
                switch (code)
                {
                    case 0:
                        CurrentStyle = TerminalEmulatorBase.CellStyle.Default;
                        break;
                    case 1:
                        CurrentStyle.Bold = true;
                        break;
                    case 21:
                    case 22:
                        CurrentStyle.Bold = false;
                        break;
                    case 4:
                        CurrentStyle.Underline = true;
                        break;
                    case 24:
                        CurrentStyle.Underline = false;
                        break;
                    case 7:
                        CurrentStyle.Inverse = true;
                        break;
                    case 27:
                        CurrentStyle.Inverse = false;
                        break;
                    case 38:
                        CurrentStyle.Foreground = 0;
                        CurrentStyle.ExtendedForeground = EncodeExtendedColor(attribute.Color);
                        break;
                    case 39:
                        CurrentStyle.Foreground = 0;
                        CurrentStyle.ExtendedForeground = 0;
                        break;
                    case 48:
                        CurrentStyle.Background = 0;
                        CurrentStyle.ExtendedBackground = EncodeExtendedColor(attribute.Color);
                        break;
                    case 49:
                        CurrentStyle.Background = 0;
                        CurrentStyle.ExtendedBackground = 0;
                        break;
                }
            }
        }
    }

    // See CellStyle.ExtendedForeground: palette indices are stored + 1 so that 0 can mean "not set".
    private static int EncodeExtendedColor(int color)
    {
        return ((color & XtermProtocol.RgbColorFlag) != 0) ? color : (color + 1);
    }

    private void ReportStatus(int request, EscapeTrace escapeTrace)
    {
        switch (request)
        {
            case 5:
                escapeTrace?.SetDescription("Status report");
                SendReply("\u001b[0n");
                break;
            case 6:
                {
                    // Cursor position report; at the pending-wrap position the cursor is still on the last column.
                    escapeTrace?.SetDescription("Cursor position report");
                    Point cursor = base.CursorPosition;
                    int column = Math.Min(cursor.X, Math.Max(_visibleColumns - 1, 0));
                    SendReply($"\u001b[{cursor.Y + 1};{column + 1}R");
                    break;
                }
        }
    }

    private void SendReply(string reply)
    {
        if (!SuppressReplies)
        {
            SendText(reply, InputKind.KeySequence);
        }
    }

    private void ResetByteState()
    {
        _byteOffset = 0;
        _partialLine = -1;
        _partialColumn = -1;
        _partialBytes = null;
    }

    // Writes a text segment of the output. In byte mode stray bytes arrive escaped (U+DC80..U+DCFF, see
    // Utf8StreamDecoder.EscapeInvalidBytes) and are joined with the bytes before them; otherwise they become U+FFFD.
    private void WriteOutputText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (!ByteColumnsActive)
        {
            // Escaped bytes still in the queue when byte mode was turned off, or on the alternate screen.
            StringBuilder replaced = null;
            for (int index = 0; index < text.Length; index++)
            {
                if (Utf8StreamDecoder.IsEscapedByte(text[index]))
                {
                    replaced = replaced ?? new StringBuilder(text);
                    replaced[index] = '\uFFFD';
                }
            }
            WriteText(_charsets.Translate(replaced?.ToString() ?? text));
            return;
        }
        int runStart = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (Utf8StreamDecoder.IsEscapedByte(text[index]))
            {
                WriteByteModeRun(text.Substring(runStart, index - runStart));
                WriteEscapedByte((byte)(text[index] - Utf8StreamDecoder.EscapedByteBase));
                runStart = index + 1;
            }
        }
        WriteByteModeRun(text.Substring(runStart));
    }

    private void WriteByteModeRun(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        SplitHalfCrossedCell();
        Point cursor = base.CursorPosition;
        if (_partialColumn >= cursor.X && _partialColumn < cursor.X + text.Length && _partialLine == ToDocumentPosition(cursor).Line)
        {
            ResetByteState();
        }
        WriteText(_charsets.Translate(text));
        _byteOffset = 0;
    }

    private void WriteEscapedByte(byte value)
    {
        SplitHalfCrossedCell();
        Point cursor = base.CursorPosition;
        int line = ToDocumentPosition(cursor).Line;
        if (_partialLine == line && _partialColumn == cursor.X - 1 && CellAt(cursor.Y, _partialColumn) == '\uFFFD' && ByteColumns.CanContinue(_partialBytes, value))
        {
            // The next byte of a letter echoed one byte at a time: it completes the letter in its own cell.
            string completed = ByteColumns.TryComplete(_partialBytes, value);
            if (completed != null)
            {
                WriteAt(new Point(_partialColumn, cursor.Y), completed, CurrentStyle);
                ResetByteState();
            }
            return;
        }
        WriteText("\uFFFD");
        _byteOffset = 0;
        if (ByteColumns.SequenceLength(value) > 0)
        {
            _partialLine = line;
            _partialColumn = cursor.X;
            _partialBytes = new System.Collections.Generic.List<byte> { value };
        }
        else
        {
            _partialLine = -1;
            _partialColumn = -1;
            _partialBytes = null;
        }
    }

    // The remote editor prints after the first bytes of a letter it stepped into: those bytes keep the cell (shown
    // as U+FFFD, completed in place if the rest arrives later) and the text continues in the next cell.
    private void SplitHalfCrossedCell()
    {
        if (_byteOffset <= 0)
        {
            return;
        }
        Point cursor = base.CursorPosition;
        byte[] prefix = ByteColumns.CellPrefix(GetLineText(cursor.Y), cursor.X, _byteOffset);
        int line = ToDocumentPosition(cursor).Line;
        _byteOffset = 0;
        WriteText("\uFFFD");
        if (prefix.Length > 0 && ByteColumns.SequenceLength(prefix[0]) > 0)
        {
            _partialLine = line;
            _partialColumn = cursor.X;
            _partialBytes = new System.Collections.Generic.List<byte>(prefix);
        }
    }

    private char CellAt(int row, int column)
    {
        string line = GetLineText(row);
        return (line != null && column >= 0 && column < line.Length) ? line[column] : ' ';
    }

    // Moves the cursor back by a number of bytes of the remote editor; returns the bytes left at the line start.
    private int MoveBackBytes(int bytes)
    {
        Point cursor = base.CursorPosition;
        int column = Math.Min(cursor.X, _visibleColumns);
        int offset = _byteOffset;
        int remaining = ByteColumns.MoveBack(GetLineText(cursor.Y), ref column, ref offset, bytes);
        base.CursorPosition = new Point(column, cursor.Y);
        _byteOffset = offset;
        return remaining;
    }

    private void MoveForwardBytes(int bytes)
    {
        Point cursor = base.CursorPosition;
        int column = cursor.X;
        int offset = _byteOffset;
        ByteColumns.MoveForward(GetLineText(cursor.Y), ref column, ref offset, bytes, Math.Max(_visibleColumns - 1, 0));
        base.CursorPosition = new Point(column, cursor.Y);
        _byteOffset = offset;
    }

    // Byte mode: the terminal's copy of the remote command line while the user types (see ByteColumns.LinePrediction).
    private ByteColumns.LinePrediction _prediction;

    private DateTime _predictionKeyTime;

    // After a pause the screen has caught up with the remote line, and the copy is taken from it again.
    private static readonly TimeSpan PredictionLifetime = TimeSpan.FromSeconds(1);

    // The prediction for the next key: the current one, or a new one taken from the screen. Null outside byte mode
    // and when the remote cursor is inside a character (the screen cannot say where).
    private ByteColumns.LinePrediction GetPrediction()
    {
        if (!ByteColumnsActive)
        {
            _prediction = null;
            return null;
        }
        DateTime now = DateTime.UtcNow;
        if (_prediction == null || now - _predictionKeyTime > PredictionLifetime)
        {
            FlushOutput();
            _prediction = null;
            if (_byteOffset == 0)
            {
                Point cursor = base.CursorPosition;
                _prediction = new ByteColumns.LinePrediction(GetLineText(cursor.Y), Math.Min(cursor.X, _visibleColumns));
            }
        }
        _predictionKeyTime = now;
        return _prediction;
    }

    // How many times Backspace, Left, Right or Delete must be sent: once per UTF-8 byte of the character it crosses
    // in byte mode, once otherwise.
    private int ByteKeyCount(Keys key)
    {
        ByteColumns.LinePrediction prediction = GetPrediction();
        if (prediction == null)
        {
            return KeyRepeatCount(key == Keys.Back || key == Keys.Left);
        }
        int count;
        switch (key)
        {
            case Keys.Back:
                count = prediction.Backspace();
                break;
            case Keys.Left:
                count = prediction.Left();
                break;
            case Keys.Right:
                count = prediction.Right();
                break;
            default:
                count = prediction.Delete();
                break;
        }
        if (count <= 0)
        {
            // The start of the line (or of its screen row): the copy cannot follow further.
            _prediction = null;
            return 1;
        }
        return count;
    }

    public override void ClearOutput()
    {
        DropPrediction();
        base.ClearOutput();
    }

    // A key the copy of the command line cannot follow (Enter, Tab, history, Ctrl+..., a paste).
    private void DropPrediction()
    {
        _prediction = null;
    }

    // How many times a key that crosses one character must be sent (byte mode), counted on the screen; 1 otherwise.
    private int KeyRepeatCount(bool backward)
    {
        if (!ByteColumnsActive)
        {
            return 1;
        }
        // Count on the screen as the remote side has drawn it so far.
        FlushOutput();
        Point cursor = base.CursorPosition;
        string line = GetLineText(cursor.Y);
        int column = Math.Min(cursor.X, _visibleColumns);
        return backward ? ByteColumns.BackwardKeyCount(line, column, _byteOffset) : ByteColumns.ForwardKeyCount(line, column, _byteOffset);
    }

    private static string Repeat(string text, int count)
    {
        if (count <= 1)
        {
            return text;
        }
        StringBuilder result = new StringBuilder(text.Length * count);
        for (int index = 0; index < count; index++)
        {
            result.Append(text);
        }
        return result.ToString();
    }

    private void SaveCursor()
    {
        _savedCursor = base.CursorPosition;
        _savedStyle = CurrentStyle;
    }

    private void RestoreCursor()
    {
        base.CursorPosition = _savedCursor;
        CurrentStyle = _savedStyle;
    }

    /// <summary>DECSTR: resets the modes and attributes, but keeps the screen contents and the cursor position.</summary>
    private void SoftReset()
    {
        Point cursor = base.CursorPosition;
        ClearScrollRegion();
        base.CursorPosition = cursor;
        ResetModes();
    }

    private void ResetModes()
    {
        ResetByteState();
        _applicationCursorKeys = false;
        _applicationKeypad = false;
        _insertMode = false;
        HideCaret = false;
        MouseMode = MouseReportingMode.Off;
        Array.Clear(_privateModes, 0, _privateModes.Length);
        Array.Clear(_savedPrivateModes, 0, _savedPrivateModes.Length);
        CurrentStyle = TerminalEmulatorBase.CellStyle.Default;
        _savedCursor = Point.Empty;
        _savedStyle = TerminalEmulatorBase.CellStyle.Default;
        _charsets.Reset();
    }

    private void ResetFromStream()
    {
        _resetFromStream = true;
        try
        {
            Reset();
        }
        finally
        {
            _resetFromStream = false;
        }
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
                if (AppSettings != null && AppSettings.ShowCtrlInsertWarnings && ThemedMessageBox.Show("The text is automatically copied to clipboard when you select it with your mouse. Ctrl-Insert will be now sent to remote computer.\nDo you want to keep being notified about this when you press Ctrl-Insert?", "Terminal", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No)
                {
                    AppSettings.ShowCtrlInsertWarnings = false;
                    AppSettings.Save();
                }
                CopySelection();
            }
            else if (e.Shift && !e.Control && e.KeyCode == Keys.Tab)
            {
                // Back-tab, used by menus, dialogs and completion lists of console programs.
                SendText("\u001b[Z", InputKind.KeySequence);
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.Space)
            {
                // Ctrl+Space sends NUL (set-mark in Emacs and readline), as in xterm.
                SendBytes(new byte[1], InputKind.KeySequence);
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
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
            // Ctrl+Left must be ESC [ 1 ; 5 D: inserting ";5" into ESC [ D used to give ESC [ ; 5 D, which shells
            // and editors do not recognise.
            text = XtermProtocol.ApplyKeyModifiers("\u001b" + text, e.Shift, e.Alt, e.Control);
            if (!e.Shift && !e.Alt && !e.Control)
            {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Delete)
                {
                    text = Repeat(text, ByteKeyCount(e.KeyCode));
                }
                else
                {
                    DropPrediction();
                }
            }
            else
            {
                DropPrediction();
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
            SendText(Repeat(BackspaceSequence, ByteColumnsActive ? ByteKeyCount(Keys.Back) : 1), InputKind.Typed);
        }
        else if (e.KeyChar == '\r' && EnterSequence != null)
        {
            DropPrediction();
            SendText(EnterSequence, InputKind.Typed);
        }
        else
        {
            if (char.IsControl(e.KeyChar))
            {
                DropPrediction();
            }
            else if (ByteColumnsActive)
            {
                GetPrediction()?.Type(new string(e.KeyChar, 1));
            }
            SendText(new string(e.KeyChar, 1), InputKind.Typed);
        }
    }

    protected void SendText(string text, InputKind inputKind)
    {
        if (DataEntered == null)
        {
            return;
        }
        Encoding encoding = InputEncoding;
        if (Dispatcher != null)
        {
            Dispatcher.PostInput(() =>
            {
                DataEntered(this, encoding.GetBytes(text), inputKind);
            });
        }
        else
        {
            DataEntered(this, encoding.GetBytes(text), inputKind);
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

    private bool IsPrivateModeSet(int mode)
    {
        return mode < _privateModes.Length && _privateModes[mode];
    }

    // While a program has mouse reporting on, Shift+mouse still selects text locally, as in xterm.
    private bool IsReportingMouse(MouseEventArgs e)
    {
        return _mouseMode != MouseReportingMode.Off && TextArea.Contains(e.Location) && (Control.ModifierKeys & Keys.Shift) == 0;
    }

    private void HandleMouseButton(MouseEventArgs e, bool pressed)
    {
        int buttonCode;
        switch (e.Button)
        {
            case MouseButtons.Left:
                buttonCode = 0;
                break;
            case MouseButtons.Middle:
                buttonCode = 1;
                break;
            case MouseButtons.Right:
                buttonCode = 2;
                break;
            default:
                return;
        }
        switch (_mouseMode)
        {
            case MouseReportingMode.X10:
                if (!pressed)
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
            default:
                return;
        }
        SendMouseReport(buttonCode, HitTestCell(e.Location), release: !pressed);
    }

    private void HandleMouseWheel(MouseEventArgs e)
    {
        int wheelCode = 64 + ((e.Delta <= 0) ? 1 : 0);
        MouseReportingMode mouseReportingMode = _mouseMode;
        if (mouseReportingMode != MouseReportingMode.X10 && (uint)(mouseReportingMode - 1000) <= 3u)
        {
            wheelCode |= MouseModifierBits;
            SendMouseReport(wheelCode, HitTestCell(e.Location), release: false);
        }
    }

    private void HandleMouseMove(MouseEventArgs e)
    {
        // Motion reports carry the held button, or 3 ("no button") for plain motion in any-event mode.
        int buttonCode;
        switch (e.Button)
        {
            case MouseButtons.Left:
                buttonCode = 0;
                break;
            case MouseButtons.Middle:
                buttonCode = 1;
                break;
            case MouseButtons.Right:
                buttonCode = 2;
                break;
            default:
                buttonCode = 3;
                break;
        }
        switch (_mouseMode)
        {
            case MouseReportingMode.ButtonEvent:
                if (buttonCode == 3)
                {
                    return;
                }
                break;
            case MouseReportingMode.AnyEvent:
                break;
            default:
                return;
        }
        Point point = HitTestCell(e.Location);
        if (!(point == _lastMouseCell))
        {
            _lastMouseCell = point;
            SendMouseReport(buttonCode | 32 | MouseModifierBits, point, release: false);
        }
    }

    private void SendMouseReport(int buttonCode, Point cell, bool release)
    {
        // A click in scrolled-back text or in the partial cell at the edge is reported at the nearest screen cell.
        int column = Math.Max(0, Math.Min(cell.X, _visibleColumns - 1));
        int row = Math.Max(0, Math.Min(cell.Y, ((ReadonlyTextBrowser)this)._visibleRows - 1));
        SendBytes(XtermProtocol.EncodeMouseReport(buttonCode, column, row, release, IsPrivateModeSet(SgrMouseMode)), InputKind.MouseReport);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (IsReportingMouse(e))
        {
            if (!Focused)
            {
                Focus();
            }
            HandleMouseButton(e, pressed: true);
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
        if (IsReportingMouse(e))
        {
            HandleMouseButton(e, pressed: false);
        }
        else
        {
            base.OnMouseUp(e);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsReportingMouse(e))
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
        DropPrediction();
        try
        {
            if (!UsesTerminalInput)
            {
                base.Paste();
                return;
            }
            // With bracketed paste the program gets the text as one piece and decides itself what to do with the line
            // breaks (bash, zsh, vim and nano do not run or auto-indent it), so only without it the lines run at once.
            bool bracketed = IsPrivateModeSet(BracketedPasteMode);
            string text = PasteGuard.Prepare(Win32Clipboard.GetText(Handle), AppSettings, linesRunAtOnce: !bracketed, owner: this);
            if (text != null)
            {
                text = XtermProtocol.PreparePaste(text, PasteGuard.ResolveLineBreak(AppSettings, PasteLineBreak), bracketed);
                if (!string.IsNullOrEmpty(text))
                {
                    SendText(text, InputKind.Paste);
                }
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void EnqueueOutput(string text)
    {
        // A closed tab may still get the last chunks of its channel: nothing would ever flush them.
        if (IsDisposed || string.IsNullOrEmpty(text))
        {
            return;
        }
        lock (_outputLock)
        {
            // Appending to a StringBuilder keeps a burst of output linear: while the UI thread is busy, every new
            // chunk used to copy all the text queued before it.
            _pendingOutput.Append(text);
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
        string text = null;
        lock (_outputLock)
        {
            if (_pendingOutput.Length != 0)
            {
                text = _pendingOutput.ToString();
                _pendingOutput.Clear();
            }
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
        if (IsReportingMouse(e))
        {
            HandleMouseWheel(e);
        }
        else if ((Control.ModifierKeys & Keys.Alt) != Keys.Alt && AppSettings != null && AppSettings.EmulateDirectionKeysWithMouseWheel)
        {
            string text = _keyMap?.GetEscapeSequence((e.Delta < 0) ? Keys.Down : Keys.Up, _applicationKeypad, this._applicationCursorKeys, isNumPadKey: false);
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
        _charsets.Reset();
    }

    public override void Reset()
    {
        // A full reset returns to the normal screen (a program that crashed in the alternate one leaves it there)
        // and drops the scroll region, tab stops and modes along with the screen contents.
        if (base.ActiveBufferIndex != 0)
        {
            base.ActiveBufferIndex = 0;
        }
        ClearScrollRegion();
        Tabs = new TabStops();
        base.Reset();
        ResetModes();
        if (!_resetFromStream)
        {
            // Output queued after an ESC c from the remote side still has to be shown; only a reset requested
            // locally discards what has not been processed yet.
            lock (_outputLock)
            {
                _pendingOutput.Clear();
            }
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
