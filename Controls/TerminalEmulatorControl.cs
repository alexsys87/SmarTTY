using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using SmarTTY.Terminal;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.Diagnostics;
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
    /// <summary>
    /// The terminal shows a log (tail -F): the lines are coloured from their level (ERROR, WARN, [error], level=warn) to the
    /// end, unless the program wrote them in colours of its own.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HighlightLogLevels { get; set; }

    /// <summary>
    /// File names in the output of the programs are links (Ctrl+click opens the file in the editor). Only for a classic terminal
    /// of a Unix host; a Smart Terminal tab has links of its own (see <see cref="SshTerminalControl"/>).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FileLinks { get; set; }

    /// <summary>
    /// The http, https and ftp addresses in the output are links (Ctrl+click opens them in the browser). Also on a Windows host,
    /// where file names are no links: an address names no file of either computer.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UrlLinks { get; set; }

    /// <summary>
    /// The directory the shell reported with OSC 7 (the prompt of bash or zsh sends it on many systems), or null. Relative names in
    /// the output are resolved against it.
    /// </summary>
    public string ReportedDirectory { get; private set; }

    /// <summary>
    /// Tells the directory of the shell when it did not report one (the title or the prompt, see AppEnvironment.GetCurrentDirectory);
    /// called when a link is clicked, since reading it may take a round trip to the server.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string> DirectoryFallback { get; set; }

    /// <summary>A link to a file was clicked; the path is absolute when the directory of the shell is known.</summary>
    public event EventHandler<OpenFileEventArgs> FileLinkClicked;

    private string _linkExtensionsText;

    private System.Collections.Generic.HashSet<string> _linkExtensions;

    private sealed class ClassicLinkHost : IOutputLinkHost
    {
        private readonly TerminalEmulatorControl _terminal;

        public ClassicLinkHost(TerminalEmulatorControl terminal)
        {
            _terminal = terminal;
        }

        public bool IsLinkExtension(string extension)
        {
            return _terminal.IsLinkExtension(extension);
        }

        public void RequestOpenFile(string path, int? line, int? column, CodeEditing.ColumnUnit unit)
        {
            _terminal.OpenLinkedFile(path, line, column, unit);
        }

        public void RequestOpenUrl(string url)
        {
            UrlOpener.Open(url);
        }
    }

    private bool IsLinkExtension(string extension)
    {
        string text = AppSettings?.VirtualTerminal?.AutoLinkExtensions;
        if (!string.Equals(text, _linkExtensionsText, StringComparison.Ordinal) || _linkExtensions == null)
        {
            _linkExtensionsText = text;
            _linkExtensions = VirtualTerminalSettings.ParseAutoLinkExtensions(text);
        }
        return _linkExtensions.Contains(extension);
    }

    private void OpenLinkedFile(string path, int? line, int? column, CodeEditing.ColumnUnit unit)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            // A name relative to a directory that the shell never reported: the directory it is in now.
            string directory = null;
            try
            {
                directory = DirectoryFallback?.Invoke();
            }
            catch (Exception)
            {
            }
            path = RemotePathUtils.Combine(directory, path);
        }
        FileLinkClicked?.Invoke(this, new OpenFileEventArgs
        {
            Path = path,
            Line = line,
            Column = column,
            ColumnUnit = unit
        });
    }

    // Only an ordinary screen of a shell has links: a full-screen program (an editor, a pager) draws its own text, and one that
    // takes the mouse needs the clicks.
    private bool LinksAvailable => (FileLinks || UrlLinks) && ActiveBufferIndex == 0 && _mouseMode == MouseReportingMode.Off;

    internal override ILinkScanner CreateLinkScanner()
    {
        if (HighlightLogLevels)
        {
            return new SmarTTY.Terminal.OutputLinkScanner(null, "/", null, links: false, highlight: false, logLevels: true);
        }
        if (LinksAvailable)
        {
            return new SmarTTY.Terminal.OutputLinkScanner(new ClassicLinkHost(this), ReportedDirectory ?? "", null, links: FileLinks, highlight: false, logLevels: false,
                columnSetting: AppSettings?.VirtualTerminal?.CompilerColumns ?? CodeEditing.ColumnUnit.Auto, strictAbsolute: true, urls: UrlLinks);
        }
        return base.CreateLinkScanner();
    }

    private ILinkTarget GetFileLinkAt(int line, Point point)
    {
        LineInfo lineInfo = ActiveBuffer?.Lines?.GetLine(line);
        if (lineInfo?.Cells != null)
        {
            TextDocument.TextPosition textPosition = HitTest(point);
            if (textPosition.Column >= 0 && textPosition.Column < lineInfo.Cells.Length)
            {
                return lineInfo.Cells[textPosition.Column]._link.Target;
            }
        }
        return null;
    }

    // Plain clicks select text in a classic terminal, so a link needs Ctrl.
    protected override HoverTarget GetHoverTarget(int line, Point point)
    {
        if (LinksAvailable && (Control.ModifierKeys & Keys.Control) != 0 && (Control.ModifierKeys & Keys.Shift) == 0)
        {
            ILinkTarget linkTarget = GetFileLinkAt(line, point);
            if (linkTarget != null && linkTarget.IsActive)
            {
                return new HoverTarget
                {
                    Cursor = Cursors.Hand,
                    ClickAction = linkTarget.Activate
                };
            }
        }
        return base.GetHoverTarget(line, point);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if ((FileLinks || UrlLinks) && e.KeyCode == Keys.ControlKey)
        {
            RefreshHoverTarget();
        }
    }

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

    /// <summary>
    /// Collects what one escape sequence meant and, when it is disposed, hands that to <c>TerminalEmulatorBase.Trace</c>
    /// as a single line. It is only ever created in a Debug build (see <c>ProcessOutput</c>), and every method that
    /// feeds it is <see cref="ConditionalAttribute"/> on DEBUG, so a Release build neither calls nor allocates any of
    /// this and the description strings are not even compiled in.
    /// </summary>
    internal class EscapeTrace : IDisposable, IEscapeTrace
    {
        private string _warning;

        private string _description;

        private string _raw;

        private int _position;

        private TerminalEmulatorControl _terminal;

        public EscapeTrace(TerminalEmulatorControl terminalEmulatorControl)
        {
            _terminal = terminalEmulatorControl;
        }

        public void Dispose()
        {
            string prefix = "[ESC @" + _position + "] ";
            if (_description != null)
            {
                _terminal.Trace(prefix + _description);
            }
            else if (_warning != null)
            {
                _terminal.Trace(prefix + "WARNING: " + _warning + " (" + _raw + " )");
            }
            else if (_raw != null)
            {
                _terminal.Trace(prefix + _raw);
            }
            else
            {
                _terminal.Trace(prefix + "???");
            }
        }

        [Conditional("DEBUG")]
        internal void SetPosition(int position)
        {
            _position = position;
        }

        [Conditional("DEBUG")]
        internal void SetDescription(string text)
        {
            _description = text;
        }

        [Conditional("DEBUG")]
        internal void SetWarning(string text)
        {
            _warning = text;
        }

        [Conditional("DEBUG")]
        internal void SetRaw(string text)
        {
            this._raw = text;
        }

        void IEscapeTrace.SetPosition(int position)
        {
            _position = position;
        }

        void IEscapeTrace.SetDescription(string text)
        {
            _description = text;
        }

        void IEscapeTrace.SetWarning(string text)
        {
            _warning = text;
        }

        void IEscapeTrace.SetRaw(string text)
        {
            _raw = text;
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

    private XTermKeyMap _keyMap;

    public const string TerminalType = "xterm";

    public GlobalSettings AppSettings;

    public static readonly string ResetSequence = "\u001b[0m";

    // The state that the escape sequences set and that is not the screen: the modes, the character sets, the saved cursor and the start of
    // an escape sequence that has not arrived whole.
    private readonly TerminalModes _modes = new TerminalModes();

    // What the escape sequences do (the CSI commands, the OSC strings, the saved cursor); it works on this control as its screen and host.
    private readonly TerminalEmulator _emulator;

    private const int BracketedPasteMode = 2004;

    private const int SgrMouseMode = 1006;

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

    private DecCharsets _charsets => _modes.Charsets;

    // The numbers come from the output of the remote side, negative ones included: see PrivateModeTable.
    private PrivateModeTable _privateModes => _modes.PrivateModes;

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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowUnrecognizedAsHex { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxChunkLength { get; set; }

    [Category("Terminal")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BeepMode BellMode { get; set; }

    [Category("Terminal")]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BellSoundFile { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TerminalEventDispatcher Dispatcher { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ForwardAltF4 { get; set; }

    protected virtual bool UnusedOption => false;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CaptureDialogKeys { get; set; }

    public event TerminalSizeChangedHandler TerminalSizeChanged;

    public event TerminalDataHandler DataEntered;

    /// <summary>Sends text to the remote side as if the user had typed it (a line ends with "\r").</summary>
    public void TypeText(string text)
    {
        SendText(text, InputKind.Typed);
    }

    /// <summary>
    /// Puts a command into the command line without running it (Tools > Quick commands): the user checks it and presses Enter.
    /// A classic terminal types it to the shell; the Smart Terminal inserts it into its own input line.
    /// </summary>
    public virtual void InsertCommand(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            TypeText(text);
        }
    }

    /// <summary>
    /// Raised with every chunk of output as it arrives, escape sequences included, before it is drawn. The chunk may end in the
    /// middle of an escape sequence.
    /// </summary>
    public event Action<string> OutputReceived;

    /// <summary>
    /// The user chose to send a very large paste as a file instead of typing it: the text to upload to the current directory
    /// of the terminal (raised on the UI thread).
    /// </summary>
    public event Action<TerminalEmulatorControl, string> LargePasteUploadRequested;

    // From this size a paste is sent in pieces, one after another, so that the window does not wait for the whole text.
    private const int AsyncPasteThreshold = 16 * 1024;

    private const int AsyncPasteChunkSize = 4096;

    private PasteMode _pasteMode;

    private PasteJob _pasteJob;

    /// <summary>The way the paste that is being done now was asked for (see <see cref="PasteSpecial"/>).</summary>
    protected PasteMode PasteModeInUse => _pasteMode;

    public TerminalEmulatorControl()
    {
        _emulator = new TerminalEmulator(this, this, _modes);
        InitializeComponent();
        Disposed += (object sender, EventArgs e) => _pasteJob?.Abort();
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
        OutputReceived?.Invoke(output);
        if (_modes.IncompleteEscape != null)
        {
            output = _modes.IncompleteEscape + output;
            _modes.IncompleteEscape = null;
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
                            // Escape diagnostics exist in a Debug build only. The tracing object is created just
                            // around the sequence, so its Dispose() reports what the sequence meant right after
                            // it is parsed. A Release build compiles the whole thing out.
#if DEBUG
                            using EscapeTrace escapeTrace = new EscapeTrace(this);
#else
                            EscapeTrace escapeTrace = null;
#endif
                            int consumed = _emulator.ParseEscapeSequence(output, charIndex, out var text, escapeTrace);
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
            TerminalSizeChanged(this, _visibleColumns, _visibleRows);
            return;
        }
        Dispatcher.PostResize(() =>
        {
            TerminalSizeChanged(this, _visibleColumns, _visibleRows);
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((FileLinks || UrlLinks) && e.KeyCode == Keys.ControlKey)
        {
            RefreshHoverTarget();
        }
        if (TryHandlePasteShortcut(e))
        {
            return;
        }
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
                    TopLine -= _visibleRows;
                }
                else
                {
                    TopLine += _visibleRows;
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
        string text = _keyMap.GetEscapeSequence(e.KeyCode, _modes.ApplicationKeypad, _modes.ApplicationCursorKeys, numPad);
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

    private const int WM_CONTEXTMENU = 0x007B;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_CONTEXTMENU && HandleRightClick(ref message))
        {
            return;
        }
        if (message.Msg == 256)
        {
            _extendedKey = (message.LParam.ToInt64() & 0x1000000) != 0;
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
        return _privateModes.IsSet(mode);
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
        int row = Math.Max(0, Math.Min(cell.Y, _visibleRows - 1));
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
        if (e.Button == MouseButtons.Middle && PasteOptions.MiddleClickPastes)
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

    /// <summary>
    /// Raised when Paste finds files (copied in Explorer) and no text in the clipboard: the owner of the tab decides what
    /// to do with them (upload them, paste their paths).
    /// </summary>
    public event Action<TerminalEmulatorControl, string[]> ClipboardFilesPasted;

    private PasteSettings PasteOptions => PasteGuard.OptionsOf(AppSettings);

    public override void Paste()
    {
        string text = ReadClipboardText();
        if (string.IsNullOrEmpty(text) && TryPasteClipboardFiles())
        {
            return;
        }
        PasteText(text);
    }

    private bool TryPasteClipboardFiles()
    {
        if (ClipboardFilesPasted == null || !PasteOptions.OfferClipboardFiles || !CodeEditorPP.IsWindows() || !Win32Clipboard.HasFiles())
        {
            return false;
        }
        string[] files = Win32Clipboard.GetFiles(Handle);
        if (files == null || files.Length == 0)
        {
            return false;
        }
        ClipboardFilesPasted(this, files);
        return true;
    }

    /// <summary>Shows the menu with the clipboard, the texts pasted lately and the snippets.</summary>
    public void ShowPasteMenu(Point? clientLocation = null)
    {
        PasteMenu.Show(this, AppSettings, clientLocation, Paste, PasteText);
    }

    // Ctrl+Shift+V pastes (or opens the paste menu, as the settings say) and Ctrl+Shift+Insert opens the menu, in every
    // state of the terminal: while a program reads the keyboard and in the command line of a Smart Terminal.
    private bool TryHandlePasteShortcut(KeyEventArgs e)
    {
        if (e.Alt || !e.Control || !e.Shift)
        {
            return false;
        }
        if (e.KeyCode == Keys.V)
        {
            if (PasteOptions.CtrlShiftV == PasteSettings.ShortcutAction.HistoryMenu)
            {
                ShowPasteMenu();
            }
            else
            {
                Paste();
            }
        }
        else if (e.KeyCode == Keys.Insert)
        {
            ShowPasteMenu();
        }
        else
        {
            return false;
        }
        e.SuppressKeyPress = true;
        e.Handled = true;
        return true;
    }

    // What the right mouse button does when the settings say something else than opening the menu. Shift+right button
    // and the menu key still open the menu, and a program that reports the mouse gets the button itself.
    private bool HandleRightClick(ref Message message)
    {
        PasteSettings.MouseAction action = PasteOptions.RightClick;
        long position = message.LParam.ToInt64();
        bool fromKeyboard = position == -1 || (position & 0xFFFFFFFFL) == 0xFFFFFFFFL;
        if (action == PasteSettings.MouseAction.Menu || fromKeyboard || _mouseMode != MouseReportingMode.Off || (ModifierKeys & Keys.Shift) != 0)
        {
            return false;
        }
        if (action == PasteSettings.MouseAction.CopyOrPaste && HasSelection)
        {
            CopySelection();
            ClearSelection();
        }
        else
        {
            Paste();
        }
        return true;
    }

    public override void PasteText(string clipboardText)
    {
        DropPrediction();
        try
        {
            if (!UsesTerminalInput)
            {
                base.PasteText(clipboardText);
                return;
            }
            // With bracketed paste the program gets the text as one piece and decides itself what to do with the line
            // breaks (bash, zsh, vim and nano do not run or auto-indent it), so only without it the lines run at once.
            bool bracketed = IsPrivateModeSet(BracketedPasteMode);
            if (_pasteJob != null && _pasteJob.IsActive)
            {
                // The previous paste is still being sent.
                return;
            }
            clipboardText = PasteGuard.ApplyMode(clipboardText, _pasteMode);
            string text = PasteGuard.Prepare(clipboardText, AppSettings, linesRunAtOnce: !bracketed, owner: this);
            if (text != null)
            {
                // A COM/TCP session may use an encoding without some of the characters of the text.
                text = PasteGuard.FitToEncoding(text, InputEncoding, AppSettings, this);
            }
            if (text != null && !OfferUpload(text))
            {
                string lineBreak = PasteGuard.ResolveLineBreak(AppSettings, PasteLineBreak);
                SendPasted(XtermProtocol.PreparePaste(text, lineBreak, bracketed), lineBreak, bracketed);
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    /// <summary>
    /// Pastes the clipboard text in another way than Paste: as one line (the lines joined with a space or with &amp;&amp;); the
    /// other modes belong to the terminals that support them.
    /// </summary>
    public virtual void PasteSpecial(PasteMode mode)
    {
        _pasteMode = mode;
        try
        {
            Paste();
        }
        finally
        {
            _pasteMode = PasteMode.Normal;
        }
    }

    // A text this large is better uploaded than typed: asks, and returns true when the text must not be pasted.
    private bool OfferUpload(string text)
    {
        int thresholdKB = PasteGuard.OptionsOf(AppSettings).UploadOfferThresholdKB;
        if (thresholdKB <= 0 || LargePasteUploadRequested == null || text.Length < thresholdKB * 1024)
        {
            return false;
        }
        switch (PasteGuard.AskUpload(text.Length, this))
        {
            case DialogResult.Yes:
                LargePasteUploadRequested(this, text);
                return true;
            case DialogResult.No:
                return false;
            default:
                return true;
        }
    }

    private void SendPasted(string text, string lineBreak, bool bracketed)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (text.Length < AsyncPasteThreshold)
        {
            SendText(text, InputKind.Paste);
            return;
        }
        _pasteJob = new PasteJob(this, "Pasting", PasteModes.BuildSteps(text, AsyncPasteChunkSize, lineBreak, splitLines: false),
            (string piece, Action written) => SendTextWithCompletion(piece, InputKind.Paste, written),
            AppSettings?.Paste, slow: false,
            (PasteJob.Outcome outcome, string message) =>
            {
                _pasteJob = null;
                if (outcome == PasteJob.Outcome.Cancelled && bracketed)
                {
                    // The program waits for the end of the paste.
                    SendText(XtermProtocol.BracketedPasteEnd, InputKind.Paste);
                }
                else if (outcome == PasteJob.Outcome.Failed)
                {
                    ThemedMessageBox.Show(message, "Paste", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            });
        _pasteJob.Start();
    }

    /// <summary>Stops the paste that is being sent in pieces (Esc); false when there is none.</summary>
    protected virtual bool CancelActivePaste()
    {
        PasteJob job = _pasteJob;
        if (job == null || !job.IsActive)
        {
            return false;
        }
        job.Cancel();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && CancelActivePaste())
        {
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>As SendText, but <paramref name="written"/> is called (on any thread) when the data has been handed over.</summary>
    protected bool SendTextWithCompletion(string text, InputKind inputKind, Action written)
    {
        if (DataEntered == null)
        {
            return false;
        }
        byte[] bytes = InputEncoding.GetBytes(text);
        if (Dispatcher != null)
        {
            Dispatcher.PostInput(() =>
            {
                try
                {
                    DataEntered(this, bytes, inputKind);
                }
                finally
                {
                    written?.Invoke();
                }
            });
        }
        else
        {
            try
            {
                DataEntered(this, bytes, inputKind);
            }
            finally
            {
                written?.Invoke();
            }
        }
        return true;
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
            string text = _keyMap?.GetEscapeSequence((e.Delta < 0) ? Keys.Down : Keys.Up, _modes.ApplicationKeypad, _modes.ApplicationCursorKeys, isNumPadKey: false);
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
        _emulator.ResetModes();
        if (!_resetFromStream)
        {
            // Output queued after an ESC c from the remote side still has to be shown; only a reset requested
            // locally discards what has not been processed yet.
            lock (_outputLock)
            {
                _pendingOutput.Clear();
            }
        }
        _modes.IncompleteEscape = null;
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
