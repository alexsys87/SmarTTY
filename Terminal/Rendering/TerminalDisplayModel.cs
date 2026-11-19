using SmarTTY.CodeEditing;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// A terminal as a view without a window sees it: the emulator and its screen (<see cref="TerminalEmulator"/>, <see cref="TerminalScreen"/>),
/// the size of the grid, the line at the top of the view, the caret, the selection, and a version number that changes with everything a view
/// paints. It is the view of the screen (<see cref="ITerminalScreenView"/>) and the host of the emulator (<see cref="ITerminalHost"/>) for a
/// view that draws the cells itself (the WPF view of stage 2). The view reads it when it paints and calls it on its own thread; nothing here
/// knows a window. It also has what the WinForms <c>TerminalEmulatorControl</c> did that needs no window: the byte mode of the line
/// editors that count UTF-8 bytes (BusyBox without Unicode), the links in the output and the colours of a log, the start of a new session.
/// </summary>
internal sealed class TerminalDisplayModel : ITerminalScreenView, ITerminalHost
{
    private const int BracketedPasteMode = 2004;

    private const int SgrMouseMode = 1006;

    private readonly TerminalModes _modes = new TerminalModes();

    private readonly TerminalScreen _screen;

    private readonly TerminalEmulator _emulator;

    private EditableTextDocument _document;

    private int _columns;

    private int _rows;

    private int _topLine;

    private TextDocument.TextPosition _caret;

    private bool _caretHidden;

    private bool _hasSelection;

    private TextDocument.TextPosition _selectionAnchor;

    private TextDocument.TextPosition _selectionEnd;

    private string _title;

    private MouseReportingMode _mouseMode;

    private bool _byteOrientedLineEditing;

    // Byte mode: the remote cursor is this many bytes into the character at the cursor.
    private int _byteOffset;

    // Byte mode: a cell shown as U+FFFD that holds the first bytes of a letter the remote editor printed one byte at a time (document line,
    // column and the bytes received so far); the next bytes complete it in place.
    private int _partialLine = -1;

    private int _partialColumn = -1;

    private List<byte> _partialBytes;

    // Set while text the terminal draws on its own (a local echo) is processed: it is measured in characters, not in bytes.
    private bool _processingLocalOutput;

    // Byte mode: the terminal's copy of the remote command line while the user types (see ByteColumns.LinePrediction).
    private ByteColumns.LinePrediction _prediction;

    private DateTime _predictionKeyTime;

    // After a pause the screen has caught up with the remote line, and the copy is taken from it again.
    private static readonly TimeSpan PredictionLifetime = TimeSpan.FromSeconds(1);

    public TerminalDisplayModel(int columns, int rows)
    {
        _columns = Math.Max(columns, 1);
        _rows = Math.Max(rows, 1);
        _screen = new TerminalScreen(this);
        _screen.CreateBuffers();
        _screen.ActiveBufferIndex = 0;
        _emulator = new TerminalEmulator(_screen, this, _modes);
    }

    /// <summary>Something that a view paints changed (raised synchronously, often many times while one chunk of output is parsed).</summary>
    public event Action Changed;

    /// <summary>The emulator answers the remote side (cursor position, device attributes ...): the text to send as if it had been typed.</summary>
    public event Action<string> Reply;

    public event Action TitleChanged;

    public event Action Bell;

    public event Action MouseModeChanged;

    /// <summary>Raised with every chunk of output before it is parsed (escape sequences included).</summary>
    public event Action<string> OutputReceived;

    /// <summary>Changes with every change that a view paints (the cells, the scroll position, the caret, the selection).</summary>
    public int Version { get; private set; }

    /// <summary>The number of columns of a tab.</summary>
    public int TabWidth { get; set; } = 8;

    /// <summary>The longest chunk of output that is shown whole; a longer one is cut to this many characters from its end (0: no limit).</summary>
    public int MaxChunkLength { get; set; }

    /// <summary>A control character that the terminal does nothing about is shown as its number.</summary>
    public bool ShowUnrecognizedAsHex { get; set; }

    /// <summary>The most lines the normal screen keeps above the screen.</summary>
    public int ScrollbackLimit
    {
        get => _screen.ScrollbackLimit;
        set => _screen.ScrollbackLimit = value;
    }

    /// <summary>No answers to the queries of the remote side are sent (a mirror of a terminal whose own emulator answers).</summary>
    public bool SuppressReplies { get; set; }

    public int Columns => _columns;

    public int Rows => _rows;

    /// <summary>The document of the active buffer: one line of text per row, the scrollback above the screen.</summary>
    public EditableTextDocument Document => _document;

    /// <summary>The styles of the lines of the active buffer.</summary>
    public LineInfoStore Lines => _screen.ActiveBuffer.Lines;

    /// <summary>0: the normal screen, 1: the alternate screen (vi, mc, htop).</summary>
    public int ActiveBufferIndex => _screen.ActiveBufferIndex;

    /// <summary>The line of the document at the top of the screen of the remote side.</summary>
    public int ScreenTopLine => _screen.ActiveBuffer.FirstRow;

    /// <summary>The first line of the document in the view (the scroll position).</summary>
    public int TopLine
    {
        get => _topLine;
        set
        {
            int clamped = Math.Max(0, Math.Min(value, MaxTopLine));
            if (clamped != _topLine)
            {
                _topLine = clamped;
                OnChanged();
            }
        }
    }

    /// <summary>The largest scroll position: the last line of the document at the bottom of the view.</summary>
    public int MaxTopLine => Math.Max(0, Math.Max(_document.LineCount - _rows, _screen.ActiveBuffer.FirstRow));

    /// <summary>The place of the caret in the document.</summary>
    public TextDocument.TextPosition Caret => _promptCaret ?? _caret;

    // The caret of a command line that the terminal edits itself (the Smart Terminal at its prompt); null: the cursor of the screen.
    private TextDocument.TextPosition? _promptCaret;

    /// <summary>
    /// Shows the caret at a place of the document instead of at the cursor of the screen (the command line of the Smart Terminal, which is
    /// edited here and not by a program), and keeps that place in the view; null gives the caret back to the screen.
    /// </summary>
    public void SetPromptCaret(TextDocument.TextPosition? position)
    {
        _promptCaret = position;
        if (position.HasValue)
        {
            EnsureLineVisible(position.Value.Line);
        }
        OnChanged();
    }

    /// <summary>
    /// Replaces the lines of the document from <paramref name="startLine"/> to its end with <paramref name="rows"/> (the rows of the command
    /// line of the Smart Terminal, without styles). The styles of the replaced lines go with them.
    /// </summary>
    public void ReplaceDocumentTail(int startLine, IList<string> rows)
    {
        EditableTextDocument document = _document;
        startLine = Math.Max(0, startLine);
        if (startLine < document.LineCount)
        {
            document.RemoveLines(startLine, document.LineCount - startLine);
        }
        Lines.RemoveLines(startLine, int.MaxValue);
        while (document.LineCount < startLine)
        {
            document.AppendLine("");
        }
        foreach (string row in rows)
        {
            document.AppendLine(row ?? "");
        }
        if (_hasSelection)
        {
            _selectionAnchor = Clamp(_selectionAnchor);
            _selectionEnd = Clamp(_selectionEnd);
        }
        OnChanged();
    }

    /// <summary>
    /// The output of a command starts on a new line at the end of the document: the screen of the remote side begins there with the cursor
    /// at its top left corner, and the caret is the cursor again (what <c>SshTerminalControl.BeginExecution</c> did to its buffer).
    /// </summary>
    public void BeginOutputAtEnd()
    {
        _promptCaret = null;
        _document.AppendLine("");
        _screen.ActiveBuffer.FirstRow = _document.LineCount - 1;
        _screen.Cursor = new GridPoint(0, 0);
        _screen.UpdateCaret();
        EnsureLineVisible(_document.LineCount - 1);
        OnChanged();
    }

    /// <summary>Sets the title as a program would (the Smart Terminal shows the user, the host and the directory there).</summary>
    public void SetTitle(string title)
    {
        ((ITerminalHost)this).Title = title;
    }

    /// <summary>Goes back to the normal screen (the alternate one belongs to a program that has ended).</summary>
    public void SwitchToNormalScreen()
    {
        if (_screen.ActiveBufferIndex != 0)
        {
            _screen.ActiveBufferIndex = 0;
        }
    }

    /// <summary>The next output starts a new link (a new command).</summary>
    public void ResetLinkTracking()
    {
        _screen.ResetLinkTracking();
    }

    /// <summary>Forgets a half-crossed character of byte mode (a new command starts).</summary>
    public void ResetByteColumns()
    {
        ResetByteState();
    }

    /// <summary>Makes the scanners of the links of the output (the Smart Terminal: the scanner of the command that runs); null: the settings of this model decide.</summary>
    internal Func<ILinkScanner> LinkScannerFactory { get; set; }

    private void EnsureLineVisible(int line)
    {
        if (line < _topLine)
        {
            _topLine = Math.Max(0, line);
        }
        else if (line >= _topLine + _rows)
        {
            _topLine = Math.Max(0, line - _rows + 1);
        }
    }

    /// <summary>The program hid the caret (CSI ? 25 l).</summary>
    public bool CaretHidden => _caretHidden;

    public string Title => _title;

    public MouseReportingMode MouseMode => _mouseMode;

    public bool ApplicationCursorKeys => _modes.ApplicationCursorKeys;

    public bool ApplicationKeypad => _modes.ApplicationKeypad;

    public bool BracketedPaste => _modes.PrivateModes.IsSet(BracketedPasteMode);

    public bool SgrMouse => _modes.PrivateModes.IsSet(SgrMouseMode);

    public string ReportedDirectory { get; private set; }

    public bool HasSelection => _hasSelection && _selectionAnchor != _selectionEnd;

    /// <summary>The selection from its start to its end in the order of the document (valid when <see cref="HasSelection"/>).</summary>
    public void GetSelection(out TextDocument.TextPosition start, out TextDocument.TextPosition end)
    {
        if (_selectionAnchor < _selectionEnd)
        {
            start = _selectionAnchor;
            end = _selectionEnd;
        }
        else
        {
            start = _selectionEnd;
            end = _selectionAnchor;
        }
    }

    /// <summary>The cursor on the screen of the remote side (column, row).</summary>
    public GridPoint CursorPosition => _screen.Cursor;

    /// <summary>The line of the document the cursor is in.</summary>
    public int CursorDocumentLine => _screen.CursorDocumentLine;

    /// <summary>The text of a row of the screen of the remote side (0 is its top row).</summary>
    public string GetScreenLineText(int row)
    {
        return _screen.GetLineText(row);
    }

    /// <summary>The text wraps at the right edge (otherwise the line goes on past it).</summary>
    public bool AutoWrap
    {
        get => _screen.AutoWrap;
        set
        {
            _screen.AutoWrap = value;
            OnChanged();
        }
    }

    /// <summary>
    /// The remote line editor counts UTF-8 bytes rather than characters (BusyBox ash without Unicode support, as on OpenWrt): on the normal
    /// screen its cursor moves are turned from bytes into cells, and Backspace, Delete, Left and Right are repeated once per byte of the
    /// letter they cross (<see cref="ByteKeyCount"/>). The output must then be decoded with the stray bytes escaped (Utf8StreamDecoder).
    /// </summary>
    public bool ByteOrientedLineEditing
    {
        get => _byteOrientedLineEditing;
        set
        {
            _byteOrientedLineEditing = value;
            ResetByteState();
        }
    }

    private bool ByteColumnsActive => _byteOrientedLineEditing && _screen.ActiveBufferIndex == 0 && !_processingLocalOutput;

    /// <summary>Called before the keys of byte mode are counted on the screen: the view parses the output that waits, so that the screen is up to date.</summary>
    public Action FlushPendingOutput { get; set; }

    /// <summary>The output of a log is coloured from its level (ERROR, WARN, level=warn) to the end of the line.</summary>
    public bool HighlightLogLevels { get; set; }

    /// <summary>File names in the output are links (Ctrl+click), as in a classic terminal of a Unix host.</summary>
    public bool FileLinks { get; set; }

    /// <summary>The http, https and ftp addresses in the output are links.</summary>
    public bool UrlLinks { get; set; }

    /// <summary>What a link does when it is clicked, and which extensions make a file name a link.</summary>
    internal IOutputLinkHost LinkHost { get; set; }

    /// <summary>How the columns of the compiler messages in the output are counted.</summary>
    public ColumnUnit CompilerColumns { get; set; } = ColumnUnit.Auto;

    // Only an ordinary screen of a shell has links: a full-screen program draws its own text, and one that takes the mouse needs the clicks.
    public bool LinksAvailable => LinkScannerFactory != null
        ? _mouseMode == MouseReportingMode.Off
        : (FileLinks || UrlLinks) && _screen.ActiveBufferIndex == 0 && _mouseMode == MouseReportingMode.Off;

    /// <summary>The link of a cell of the document, or null.</summary>
    internal ILinkTarget GetLinkAt(int line, int column)
    {
        LineInfo lineInfo = Lines.GetLine(line);
        if (lineInfo?.Cells != null && column >= 0 && column < lineInfo.Cells.Length)
        {
            return lineInfo.Cells[column]._link.Target;
        }
        return null;
    }

    /// <summary>
    /// "Clear": removes the scrollback and everything on the screen above the line of the cursor, which moves to the top with its prompt.
    /// Nothing is sent to the remote side. The alternate screen belongs to the program and is left alone.
    /// </summary>
    public void ClearOutput()
    {
        DropPrediction();
        _screen.ClearOutput();
        OnChanged();
    }

    /// <summary>A new session starts in this terminal (a reconnect): the old screen is dimmed and kept, the modes of the old program are dropped.</summary>
    public void BeginNewSession()
    {
        _screen.ArchiveScreen();
        ((ITerminalHost)this).MouseMode = MouseReportingMode.Off;
        _modes.Charsets.Reset();
        OnChanged();
    }

    /// <summary>Draws text that the terminal produces itself (a local echo): in byte mode its "\b" steps back one character, not one byte.</summary>
    public void ProcessLocalOutput(string output)
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

    /// <summary>Parses output of the remote side and changes the screen (on the thread of the view).</summary>
    public void ProcessOutput(string output)
    {
        OutputReceived?.Invoke(output);
        _emulator.ProcessOutput(output);
    }

    /// <summary>The view has a new size in cells.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(columns, 1);
        rows = Math.Max(rows, 1);
        if (columns == _columns && rows == _rows)
        {
            return;
        }
        _columns = columns;
        _rows = rows;
        _screen.Resized();
        OnChanged();
    }

    /// <summary>A full reset that the user asked for: the normal screen, no scroll region, the tab stops, the modes, an empty screen.</summary>
    public void Reset()
    {
        if (_screen.ActiveBufferIndex != 0)
        {
            _screen.ActiveBufferIndex = 0;
        }
        _screen.ClearScrollRegion();
        _screen.Tabs = new TabStops();
        _screen.Reset();
        _emulator.ResetModes();
        _modes.IncompleteEscape = null;
        ResetByteState();
        DropPrediction();
        _promptCaret = null;
        OnChanged();
    }

    /// <summary>Brings the screen of the remote side into the view (a key was pressed).</summary>
    public void ScrollToScreen()
    {
        if (_promptCaret.HasValue)
        {
            // The command line of the Smart Terminal may be below the last screen of the remote side.
            int topLine = _topLine;
            EnsureLineVisible(_promptCaret.Value.Line);
            if (topLine != _topLine)
            {
                OnChanged();
            }
            return;
        }
        TopLine = _screen.ActiveBuffer.FirstRow;
    }

    /// <summary>Starts a selection at a place of the document (a press of the mouse) or moves its end (a drag).</summary>
    public void Select(TextDocument.TextPosition anchor, TextDocument.TextPosition end)
    {
        _hasSelection = true;
        _selectionAnchor = Clamp(anchor);
        _selectionEnd = Clamp(end);
        OnChanged();
    }

    public void ClearSelection()
    {
        if (_hasSelection)
        {
            _hasSelection = false;
            OnChanged();
        }
    }

    /// <summary>
    /// The selected text. A line that the terminal wrapped goes on without a line break, as the WinForms view copies it; the blanks at the
    /// end of a line that is not wrapped are dropped (the screen pads its rows with them).
    /// </summary>
    public string GetSelectedText()
    {
        if (!HasSelection)
        {
            return "";
        }
        GetSelection(out TextDocument.TextPosition start, out TextDocument.TextPosition end);
        return _document.GetRangeText(start, end, GetLineBreak);
    }

    private string GetLineBreak(int line)
    {
        return Lines.GetLine(line)?.IsWrapped == true ? "" : "\r\n";
    }

    private TextDocument.TextPosition Clamp(TextDocument.TextPosition position)
    {
        if (_document.LineCount == 0)
        {
            return new TextDocument.TextPosition(0, 0);
        }
        int line = Math.Max(0, Math.Min(position.Line, _document.LineCount - 1));
        int column = Math.Max(0, Math.Min(position.Column, _document.GetLineText(line).Length));
        return new TextDocument.TextPosition(column, line);
    }

    private void OnChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    private void EnsureCaretVisible()
    {
        int line = _caret.Line;
        if (line < _topLine)
        {
            _topLine = Math.Max(0, line);
        }
        else if (line >= _topLine + _rows)
        {
            _topLine = Math.Max(0, line - _rows + 1);
        }
    }

    // The view of the screen.

    int ITerminalScreenView.VisibleRows => _rows;

    int ITerminalScreenView.VisibleColumns => _columns;

    EditableTextDocument ITerminalScreenView.Document => _document;

    int ITerminalScreenView.TopLine
    {
        get => _topLine;
        set => TopLine = value;
    }

    bool ITerminalScreenView.AlwaysTrackLines => false;

    bool ITerminalScreenView.HideMargins => true;

    TextDocument.TextPosition ITerminalScreenView.SelectionStart => _selectionAnchor;

    bool ITerminalScreenView.HideCaret
    {
        set
        {
            if (_caretHidden != value)
            {
                _caretHidden = value;
                OnChanged();
            }
        }
    }

    bool ITerminalScreenView.TraceEnabled => false;

    void ITerminalScreenView.SetDocument(EditableTextDocument document)
    {
        _document = document;
        _hasSelection = false;
        OnChanged();
    }

    void ITerminalScreenView.SetCaret(TextDocument.TextPosition position)
    {
        if (_caret != position)
        {
            _caret = position;
            EnsureCaretVisible();
            OnChanged();
        }
    }

    void ITerminalScreenView.ForceCaretUpdate()
    {
        OnChanged();
    }

    void ITerminalScreenView.SetSelection(bool select, TextDocument.TextPosition position)
    {
        _hasSelection = select;
        _selectionAnchor = position;
        _selectionEnd = position;
        OnChanged();
    }

    void ITerminalScreenView.OnTextReplaced()
    {
        if (_hasSelection)
        {
            _selectionAnchor = Clamp(_selectionAnchor);
            _selectionEnd = Clamp(_selectionEnd);
        }
        _topLine = Math.Max(0, Math.Min(_topLine, MaxTopLine));
        OnChanged();
    }

    void ITerminalScreenView.UpdateMaxLineWidth(int line)
    {
    }

    void ITerminalScreenView.UpdateLayout()
    {
        OnChanged();
    }

    void ITerminalScreenView.Redraw()
    {
        OnChanged();
    }

    ILinkScanner ITerminalScreenView.CreateLinkScanner()
    {
        if (LinkScannerFactory != null)
        {
            return LinkScannerFactory();
        }
        if (HighlightLogLevels)
        {
            return new OutputLinkScanner(null, "/", null, links: false, highlight: false, logLevels: true);
        }
        if (LinksAvailable && LinkHost != null)
        {
            return new OutputLinkScanner(LinkHost, ReportedDirectory ?? "", null, links: FileLinks, highlight: false, logLevels: false,
                columnSetting: CompilerColumns, strictAbsolute: true, urls: UrlLinks);
        }
        return null;
    }

    void ITerminalScreenView.Trace(string text)
    {
    }

    // The host of the emulator.

    void ITerminalHost.SendReply(string reply)
    {
        if (!SuppressReplies)
        {
            Reply?.Invoke(reply);
        }
    }

    int ITerminalHost.TabWidth => TabWidth;

    bool ITerminalHost.ByteColumnsActive => ByteColumnsActive;

    int ITerminalHost.ByteOffset
    {
        set => _byteOffset = value;
    }

    void ITerminalHost.ResetByteState()
    {
        ResetByteState();
    }

    void ITerminalHost.ResetFromStream()
    {
        Reset();
    }

    void ITerminalHost.MoveForwardBytes(int bytes)
    {
        GridPoint cursor = _screen.Cursor;
        int column = cursor.X;
        int offset = _byteOffset;
        ByteColumns.MoveForward(_screen.GetLineText(cursor.Y), ref column, ref offset, bytes, Math.Max(_columns - 1, 0));
        _screen.Cursor = new GridPoint(column, cursor.Y);
        _byteOffset = offset;
    }

    // Moves the cursor back by a number of bytes of the remote editor; returns the bytes left at the line start.
    int ITerminalHost.MoveBackBytes(int bytes)
    {
        GridPoint cursor = _screen.Cursor;
        int column = Math.Min(cursor.X, _columns);
        int offset = _byteOffset;
        int remaining = ByteColumns.MoveBack(_screen.GetLineText(cursor.Y), ref column, ref offset, bytes);
        _screen.Cursor = new GridPoint(column, cursor.Y);
        _byteOffset = offset;
        return remaining;
    }

    int ITerminalHost.MaxChunkLength => MaxChunkLength;

    bool ITerminalHost.ShowUnrecognizedAsHex => ShowUnrecognizedAsHex;

    void ITerminalHost.WriteOutputText(string text)
    {
        WriteOutputText(text);
    }

    void ITerminalHost.Bell()
    {
        Bell?.Invoke();
    }

    void ITerminalHost.Trace(string text)
    {
    }

    IEscapeTrace ITerminalHost.BeginEscapeTrace()
    {
        return null;
    }

    MouseReportingMode ITerminalHost.MouseMode
    {
        set
        {
            if (_mouseMode != value)
            {
                _mouseMode = value;
                MouseModeChanged?.Invoke();
            }
        }
    }

    string ITerminalHost.Title
    {
        set
        {
            if (_title != value)
            {
                _title = value;
                TitleChanged?.Invoke();
            }
        }
    }

    string ITerminalHost.ReportedDirectory
    {
        set => ReportedDirectory = value;
    }

    // Byte mode (moved from TerminalEmulatorControl as it was, with the screen in place of the control).

    private void ResetByteState()
    {
        _byteOffset = 0;
        _partialLine = -1;
        _partialColumn = -1;
        _partialBytes = null;
    }

    // Writes a text segment of the output. In byte mode stray bytes arrive escaped (U+DC80..U+DCFF, see Utf8StreamDecoder.EscapeInvalidBytes)
    // and are joined with the bytes before them; otherwise they become U+FFFD.
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
            _screen.WriteText(_modes.Charsets.Translate(replaced?.ToString() ?? text));
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
        GridPoint cursor = _screen.Cursor;
        if (_partialColumn >= cursor.X && _partialColumn < cursor.X + text.Length && _partialLine == _screen.ToDocumentPosition(cursor).Line)
        {
            ResetByteState();
        }
        _screen.WriteText(_modes.Charsets.Translate(text));
        _byteOffset = 0;
    }

    private void WriteEscapedByte(byte value)
    {
        SplitHalfCrossedCell();
        GridPoint cursor = _screen.Cursor;
        int line = _screen.ToDocumentPosition(cursor).Line;
        if (_partialLine == line && _partialColumn == cursor.X - 1 && CellAt(cursor.Y, _partialColumn) == '\uFFFD' && ByteColumns.CanContinue(_partialBytes, value))
        {
            // The next byte of a letter echoed one byte at a time: it completes the letter in its own cell.
            string completed = ByteColumns.TryComplete(_partialBytes, value);
            if (completed != null)
            {
                _screen.WriteAt(new GridPoint(_partialColumn, cursor.Y), completed, _screen.CurrentStyle);
                ResetByteState();
            }
            return;
        }
        _screen.WriteText("\uFFFD");
        _byteOffset = 0;
        if (ByteColumns.SequenceLength(value) > 0)
        {
            _partialLine = line;
            _partialColumn = cursor.X;
            _partialBytes = new List<byte> { value };
        }
        else
        {
            _partialLine = -1;
            _partialColumn = -1;
            _partialBytes = null;
        }
    }

    // The remote editor prints after the first bytes of a letter it stepped into: those bytes keep the cell (shown as U+FFFD, completed in
    // place if the rest arrives later) and the text continues in the next cell.
    private void SplitHalfCrossedCell()
    {
        if (_byteOffset <= 0)
        {
            return;
        }
        GridPoint cursor = _screen.Cursor;
        byte[] prefix = ByteColumns.CellPrefix(_screen.GetLineText(cursor.Y), cursor.X, _byteOffset);
        int line = _screen.ToDocumentPosition(cursor).Line;
        _byteOffset = 0;
        _screen.WriteText("\uFFFD");
        if (prefix.Length > 0 && ByteColumns.SequenceLength(prefix[0]) > 0)
        {
            _partialLine = line;
            _partialColumn = cursor.X;
            _partialBytes = new List<byte>(prefix);
        }
    }

    private char CellAt(int row, int column)
    {
        string line = _screen.GetLineText(row);
        return (line != null && column >= 0 && column < line.Length) ? line[column] : ' ';
    }

    // The prediction for the next key: the current one, or a new one taken from the screen. Null outside byte mode and when the remote cursor
    // is inside a character (the screen cannot say where).
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
            FlushPendingOutput?.Invoke();
            _prediction = null;
            if (_byteOffset == 0)
            {
                GridPoint cursor = _screen.Cursor;
                _prediction = new ByteColumns.LinePrediction(_screen.GetLineText(cursor.Y), Math.Min(cursor.X, _columns));
            }
        }
        _predictionKeyTime = now;
        return _prediction;
    }

    /// <summary>
    /// How many times Backspace, Left, Right or Delete (without modifiers) must be sent: once per UTF-8 byte of the character it crosses in
    /// byte mode, once otherwise.
    /// </summary>
    public int ByteKeyCount(VirtualKey key)
    {
        ByteColumns.LinePrediction prediction = GetPrediction();
        if (prediction == null)
        {
            return KeyRepeatCount(key == VirtualKey.Back || key == VirtualKey.Left);
        }
        int count;
        switch (key)
        {
            case VirtualKey.Back:
                count = prediction.Backspace();
                break;
            case VirtualKey.Left:
                count = prediction.Left();
                break;
            case VirtualKey.Right:
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

    /// <summary>A character was typed: the copy of the command line of byte mode follows it.</summary>
    public void TypedText(string text)
    {
        if (ByteColumnsActive)
        {
            GetPrediction()?.Type(text);
        }
    }

    /// <summary>A key the copy of the command line cannot follow (Enter, Tab, history, Ctrl+..., a paste).</summary>
    public void DropPrediction()
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
        FlushPendingOutput?.Invoke();
        GridPoint cursor = _screen.Cursor;
        string line = _screen.GetLineText(cursor.Y);
        int column = Math.Min(cursor.X, _columns);
        return backward ? ByteColumns.BackwardKeyCount(line, column, _byteOffset) : ByteColumns.ForwardKeyCount(line, column, _byteOffset);
    }
}
