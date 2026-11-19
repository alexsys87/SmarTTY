using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Rendering;

/// <summary>
/// A terminal as a view without a window sees it: the emulator and its screen (<see cref="TerminalEmulator"/>, <see cref="TerminalScreen"/>),
/// the size of the grid, the line at the top of the view, the caret, the selection, and a version number that changes with everything a view
/// paints. It is the view of the screen (<see cref="ITerminalScreenView"/>) and the host of the emulator (<see cref="ITerminalHost"/>) for a
/// view that draws the cells itself (the WPF view of stage 2). The view reads it when it paints and calls it on its own thread; nothing here
/// knows a window. Byte mode (the line editing of BusyBox without Unicode) and the link scanners stay with the WinForms view for now.
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
    public TextDocument.TextPosition Caret => _caret;

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
        OnChanged();
    }

    /// <summary>Brings the screen of the remote side into the view (a key was pressed).</summary>
    public void ScrollToScreen()
    {
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

    int ITerminalHost.TabWidth => 8;

    bool ITerminalHost.ByteColumnsActive => false;

    int ITerminalHost.ByteOffset
    {
        set { }
    }

    void ITerminalHost.ResetByteState()
    {
    }

    void ITerminalHost.ResetFromStream()
    {
        Reset();
    }

    void ITerminalHost.MoveForwardBytes(int bytes)
    {
        _screen.MoveCursorBy(bytes, 0);
    }

    int ITerminalHost.MoveBackBytes(int bytes)
    {
        _screen.MoveCursorBy(-bytes, 0);
        return 0;
    }

    int ITerminalHost.MaxChunkLength => 0;

    bool ITerminalHost.ShowUnrecognizedAsHex => false;

    void ITerminalHost.WriteOutputText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _screen.WriteText(_modes.Charsets.Translate(text));
        }
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
}
