using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// The screen of a terminal: the two buffers (normal and alternate), the cursor, the style of the next text, the tab stops, the scroll region,
/// and the operations that change the cells (writing, erasing, inserting, deleting, scrolling). The control that shows it is reached through
/// <see cref="ITerminalScreenView"/>. It is single-threaded: it is called where the control called the same code before.
/// </summary>
internal sealed class TerminalScreen : ITerminalScreen, ILineInfoHost
{
    private readonly ITerminalScreenView _view;

    private readonly ScreenBuffer[] _buffers = new ScreenBuffer[2];

    private int _activeBufferIndex;

    private GridPoint _cursor;

    private bool _isBatching;

    private bool _layoutPending;

    private bool _redrawPending;

    private LinkTracker _linkTracker;

    private ScrollRegion _scrollRegion;

    public TerminalScreen(ITerminalScreenView view)
    {
        _view = view;
    }

    /// <summary>Makes the two buffers (the control calls this when it is built, not before: until then there is no active buffer).</summary>
    public void CreateBuffers()
    {
        for (int bufferIndex = 0; bufferIndex < _buffers.Length; bufferIndex++)
        {
            _buffers[bufferIndex] = new ScreenBuffer(this, (bufferIndex == 0) ? int.MaxValue : 0);
        }
    }

    /// <summary>The style that the text written next gets.</summary>
    public CellStyle CurrentStyle { get; set; }

    /// <summary>The tab stops (the terminal replaces them on a full reset, so read this each time).</summary>
    public TabStops Tabs { get; set; } = new TabStops();

    /// <summary>The text wraps to the next row at the right edge (otherwise the view scrolls sideways).</summary>
    public bool AutoWrap { get; set; } = true;

    /// <summary>The text pushes the characters after it to the right (insert mode, CSI 4 h) instead of replacing them.</summary>
    public bool InsertMode { get; set; }

    public int Rows => _view.VisibleRows;

    public int Columns => _view.VisibleColumns;

    public bool HideCaret
    {
        set => _view.HideCaret = value;
    }

    /// <summary>The cursor, a column and a row of the screen (a value outside the screen is brought back to it).</summary>
    public GridPoint Cursor
    {
        get
        {
            return _cursor;
        }
        set
        {
            if (value.X > _view.VisibleColumns)
            {
                value.X = _view.VisibleColumns;
            }
            if (value.X < 0)
            {
                value.X = 0;
            }
            if (value.Y >= _view.VisibleRows)
            {
                value.Y = _view.VisibleRows - 1;
            }
            if (value.Y < 0)
            {
                value.Y = 0;
            }
            if (!(value == _cursor))
            {
                _cursor = value;
                if (!_isBatching)
                {
                    UpdateCaret();
                }
            }
        }
    }

    /// <summary>The active buffer (null before <see cref="CreateBuffers"/>).</summary>
    public ScreenBuffer ActiveBuffer
    {
        get
        {
            if (_activeBufferIndex < 0 || _activeBufferIndex >= _buffers.Length)
            {
                return _buffers[0];
            }
            return _buffers[_activeBufferIndex];
        }
    }

    /// <summary>0 for the normal screen, 1 for the alternate one.</summary>
    public int ActiveBufferIndex
    {
        get
        {
            return _activeBufferIndex;
        }
        set
        {
            if (value >= _buffers.Length)
            {
                value = _buffers.Length - 1;
            }
            ActiveBuffer.Cursor = _cursor;
            ActiveBuffer.SavedTopLine = _view.TopLine;
            _activeBufferIndex = value;
            _view.SetDocument(_buffers[value].Document);
            Cursor = _buffers[_activeBufferIndex].Cursor;
            _view.TopLine = ActiveBuffer.SavedTopLine;
            _view.SetSelection(select: false, _view.SelectionStart);
        }
    }

    /// <summary>The most lines the normal buffer keeps above the screen.</summary>
    public int ScrollbackLimit
    {
        get
        {
            return _buffers[0].MaxScrollback;
        }
        set
        {
            _buffers[0].MaxScrollback = value;
            TrimScrollback();
        }
    }

    /// <summary>A scroll region is set (not the whole screen).</summary>
    public bool HasScrollRegion => _scrollRegion != null;

    /// <summary>The first row of the scroll region.</summary>
    public int ScrollTop
    {
        get
        {
            if (_scrollRegion == null)
            {
                return 0;
            }
            return _scrollRegion.Top;
        }
    }

    /// <summary>The last row of the scroll region.</summary>
    public int ScrollBottom
    {
        get
        {
            if (_scrollRegion == null)
            {
                return _view.VisibleRows - 1;
            }
            return _scrollRegion.Bottom;
        }
    }

    /// <summary>The line of the text the cursor is in (the rows scrolled off the screen are counted).</summary>
    public int CursorDocumentLine => ToDocumentPosition(_cursor).Line;

    /// <summary>Several changes follow that are drawn once (see <see cref="EndBatch"/>).</summary>
    public void BeginBatch()
    {
        _isBatching = true;
    }

    public void EndBatch()
    {
        _isBatching = false;
        if (_layoutPending)
        {
            _view.UpdateLayout();
            _view.Redraw();
            _layoutPending = false;
        }
        if (_redrawPending)
        {
            _redrawPending = false;
            _view.Redraw();
        }
        UpdateCaret();
    }

    /// <summary>Puts the caret of the view at the cursor (at the pending-wrap position, in the first column of the next row).</summary>
    public void UpdateCaret()
    {
        GridPoint point = _cursor;
        if (AutoWrap && point.X == _view.VisibleColumns)
        {
            point = new GridPoint(0, point.Y + 1);
        }
        TextDocument.TextPosition textPosition = EnsureCursorPosition(point);
        _view.SetCaret(textPosition);
    }

    /// <summary>The position in the text of a screen cell (the rows scrolled off the screen are counted).</summary>
    public TextDocument.TextPosition ToDocumentPosition(GridPoint point)
    {
        TextDocument.TextPosition position = new TextDocument.TextPosition(point.X, point.Y);
        position.Line += ActiveBuffer.FirstRow;
        return position;
    }

    bool ILineInfoHost.AlwaysTrackLines => _view.AlwaysTrackLines;

    bool ILineInfoHost.HideMargins => _view.HideMargins;

    int ILineInfoHost.VisibleColumns => _view.VisibleColumns;

    private TextDocument.TextPosition EnsureCursorPosition(GridPoint point)
    {
        TextDocument.TextPosition documentPosition = ToDocumentPosition(point);
        EnsureDocumentSize(documentPosition);
        return documentPosition;
    }

    private void InvalidateLayout()
    {
        if (_isBatching)
        {
            _layoutPending = true;
            return;
        }
        _view.UpdateLayout();
        _view.Redraw();
    }

    /// <summary>Draws the view again (when the batch ends, if one is open).</summary>
    public void InvalidateView()
    {
        if (_isBatching)
        {
            _redrawPending = true;
        }
        else
        {
            _view.Redraw();
        }
    }

    private void EnsureDocumentSize(TextDocument.TextPosition textPosition)
    {
        EditableTextDocument document = _view.Document;
        bool scrolled = false;
        while (textPosition.Line >= document.LineCount)
        {
            document.AppendLine("");
            scrolled = true;
        }
        int length = document.GetLineText(textPosition.Line).Length;
        if (length < textPosition.Column)
        {
            document.ReplaceInLine(textPosition.Line, length, 0, new string(' ', textPosition.Column - length));
            scrolled = true;
        }
        if (scrolled)
        {
            InvalidateLayout();
        }
    }

    /// <summary>The view was resized: the screen shows the last rows of the document, and the cursor stays on its text.</summary>
    public void Resized()
    {
        int topLine = _view.Document.LineCount - _view.VisibleRows;
        if (topLine < 0)
        {
            topLine = 0;
        }
        _view.TopLine = topLine;
        int rowDelta = topLine - ActiveBuffer.FirstRow;
        ActiveBuffer.FirstRow = topLine;
        if (rowDelta != 0)
        {
            MoveCursorBy(0, -rowDelta);
        }
        TrimScrollback();
        _view.Redraw();
    }

    public void MoveCursorBy(int deltaX, int deltaY, bool scroll = false)
    {
        GridPoint point = _cursor;
        point.X += deltaX;
        point.Y += deltaY;
        if (point.Y < 0)
        {
            point.Y = 0;
        }
        MoveCursorTo(point, scroll);
    }

    private void MoveCursorTo(GridPoint point, bool scroll = false)
    {
        bool scrolled = false;
        if ((_scrollRegion != null) & scroll)
        {
            while (point.Y > _scrollRegion.Bottom)
            {
                DeleteLines(_scrollRegion.Top, 1, scroll: true);
                point.Y--;
                _view.ForceCaretUpdate();
            }
        }
        if (point.Y >= _view.VisibleRows)
        {
            if (scroll)
            {
                ActiveBuffer.FirstRow += point.Y - _view.VisibleRows + 1;
                point.Y = _view.VisibleRows - 1;
                scrolled = true;
            }
            else
            {
                point.Y = _view.VisibleRows - 1;
            }
        }
        Cursor = point;
        EnsureCursorPosition(_cursor);
        if (scrolled)
        {
            TrimScrollback();
        }
    }

    /// <summary>Writes text at the cursor, in the current style, and moves the cursor.</summary>
    public void WriteText(string text)
    {
        WriteText(text, moveCursor: true);
    }

    /// <summary>Writes text at the cursor, in the current style, and moves the cursor unless told not to.</summary>
    public void WriteText(string text, bool moveCursor)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (_view.TraceEnabled)
            {
                _view.Trace("'" + text + "'");
            }
            GridPoint point = WriteAtCore(_cursor, text, CurrentStyle, scroll: true, InsertMode);
            if (moveCursor)
            {
                Cursor = point;
            }
        }
    }

    public void RepeatPreviousChar(int count)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(_cursor);
        string text = _view.Document.GetLineText(textPosition.Line);
        char ch = ' ';
        if (textPosition.Column > 0 && text != null && textPosition.Column <= text.Length)
        {
            ch = text[textPosition.Column - 1];
        }
        CellStyle cellStyle = default;
        LineInfo lineInfo = ActiveBuffer.Lines.GetLine(textPosition.Line);
        if (lineInfo?.Cells != null && textPosition.Column < lineInfo.Cells.Length)
        {
            cellStyle = lineInfo.Cells[textPosition.Column];
        }
        WriteAt(_cursor, new string(ch, count), cellStyle);
    }

    public void EraseInDisplay(EraseMode eraseMode)
    {
        GridPoint point = _cursor;
        switch (eraseMode)
        {
            case EraseMode.ToEnd:
                {
                    EraseInLine(eraseMode);
                    for (int line = point.Y + 1; line < _view.VisibleRows; line++)
                    {
                        ClearLineFrom(line, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.ToStart:
                {
                    EraseInLine(eraseMode);
                    for (int lineBelowCursor = 0; lineBelowCursor < point.Y; lineBelowCursor++)
                    {
                        ClearLineFrom(lineBelowCursor, 0, CurrentStyle);
                    }
                    break;
                }
            case EraseMode.All:
                {
                    for (int lineAboveCursor = 0; lineAboveCursor < _view.VisibleRows; lineAboveCursor++)
                    {
                        ClearLineFrom(lineAboveCursor, 0, CurrentStyle);
                    }
                    break;
                }
        }
    }

    public void EraseInLine(EraseMode eraseMode)
    {
        GridPoint point = _cursor;
        switch (eraseMode)
        {
            case EraseMode.ToEnd:
                ClearLineFrom(point.Y, point.X, CurrentStyle);
                break;
            case EraseMode.ToStart:
                // At the pending-wrap position (X == columns) the line ends at the last column: erasing one more
                // character would wrap onto the next line and blank its first cell.
                WriteAt(new GridPoint(0, point.Y), new string(' ', Math.Min(point.X + 1, _view.VisibleColumns)), CurrentStyle);
                break;
            case EraseMode.All:
                ClearLineFrom(point.Y, 0, CurrentStyle);
                break;
        }
    }

    public void DeleteChars(int count)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(_cursor);
        int lineLength = _view.Document.GetLineText(textPosition.Line).Length;
        if (textPosition.Column >= lineLength)
        {
            return;
        }
        count = Math.Min(count, lineLength - textPosition.Column);
        _view.Document.ReplaceInLine(textPosition.Line, textPosition.Column, count, "");
        ActiveBuffer.Lines.DeleteCells(_cursor, count);
        _view.Redraw();
    }

    public void InsertChars(int count)
    {
        InsertChars(count, ' ');
    }

    public void InsertChars(int count, char ch)
    {
        TextDocument.TextPosition textPosition = EnsureCursorPosition(_cursor);
        _view.Document.ReplaceInLine(textPosition.Line, textPosition.Column, 0, new string(ch, count));
        ActiveBuffer.Lines.InsertCells(_cursor, count);
        TruncateToScreenWidth(textPosition.Line);
        _view.Redraw();
    }

    // Characters pushed past the right margin by an insertion are lost, as on a real terminal; keeping them
    // would leave text beyond the screen edge that shows up again when the line is copied or the window widened.
    private void TruncateToScreenWidth(int documentLine)
    {
        if (!AutoWrap)
        {
            return;
        }
        int length = _view.Document.GetLineText(documentLine).Length;
        if (length > _view.VisibleColumns)
        {
            _view.Document.ReplaceInLine(documentLine, _view.VisibleColumns, length - _view.VisibleColumns, "");
        }
    }

    public void WriteAt(GridPoint point, string text, CellStyle cellStyle)
    {
        WriteAtCore(point, text, cellStyle, scroll: true, insert: false);
    }

    /// <summary>Writes text at a cell, wrapping and scrolling as the options say; returns where the cursor goes next.</summary>
    public GridPoint WriteAtCore(GridPoint point, string text, CellStyle cellStyle, bool scroll, bool insert)
    {
        bool wrapped = false;
        if (AutoWrap && point.X >= _view.VisibleColumns)
        {
            ActiveBuffer.Lines.MarkWrapped(point.Y);
            point = new GridPoint(0, point.Y + 1);
            wrapped = true;
        }
        if (scroll && point.Y >= _view.VisibleRows)
        {
            int scrollAmount = point.Y - _view.VisibleRows + 1;
            ActiveBuffer.FirstRow += scrollAmount;
            point.Y -= scrollAmount;
        }
        EnsureCursorPosition(point);
        if (AutoWrap)
        {
            int availableColumns = _view.VisibleColumns - point.X;
            if (text.Length > availableColumns)
            {
                // The parts keep the way of writing: in the insert mode every part is inserted (the text behind it moves right, and what
                // leaves the screen is lost), not written over the text that was there.
                point = WriteAtCore(point, text.Substring(0, availableColumns), cellStyle, scroll, insert);
                int chunkLength;
                for (int chunkOffset = availableColumns; chunkOffset < text.Length; chunkOffset += chunkLength)
                {
                    chunkLength = Math.Min(text.Length - chunkOffset, _view.VisibleColumns);
                    point = WriteAtCore(point, text.Substring(chunkOffset, chunkLength), cellStyle, scroll, insert);
                }
                return point;
            }
        }
        TextDocument.TextPosition textPosition = ToDocumentPosition(point);
        EditableTextDocument document = _view.Document;
        int length = document.GetLineText(textPosition.Line).Length;
        LinkTracker linkTracker = _linkTracker;
        if (linkTracker == null || !linkTracker.IsActive)
        {
            ILinkScanner linkScanner = _view.CreateLinkScanner();
            if (linkScanner != null)
            {
                _linkTracker = new LinkTracker(linkScanner, textPosition);
            }
        }
        LinkCell[] linkCells = _linkTracker?.Scan(text, textPosition, wrapped);
        if (insert)
        {
            document.ReplaceInLine(textPosition.Line, textPosition.Column, 0, text);
            ActiveBuffer.Lines.InsertCells(point, text.Length);
            TruncateToScreenWidth(textPosition.Line);
        }
        else
        {
            int replaceLength = Math.Min(text.Length, length - textPosition.Column);
            document.ReplaceInLine(textPosition.Line, textPosition.Column, replaceLength, text);
        }
        ActiveBuffer.Lines.SetCells(point.Y, point.X, text.Length, cellStyle, linkCells);
        InvalidateView();
        point.X += text.Length;
        if (scroll && point.Y >= _view.VisibleRows)
        {
            int trailingScroll = point.Y - _view.VisibleRows + 1;
            ActiveBuffer.FirstRow += trailingScroll;
            point.Y -= trailingScroll;
        }
        if (!AutoWrap)
        {
            _view.UpdateMaxLineWidth(textPosition.Line);
            InvalidateLayout();
        }
        return point;
    }

    /// <summary>Clears a row from a column to its end (with the style as the background when it is not the default one).</summary>
    public void ClearLineFrom(int line, int startColumn, CellStyle cellStyle)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new GridPoint(startColumn, line));
        _view.Document.ReplaceInLine(textPosition.Line, textPosition.Column, -1, "");
        if (startColumn == 0)
        {
            ActiveBuffer.Lines.ClearLine(line);
        }
        else
        {
            // SetCells takes a screen row (it adds the scrollback offset itself).
            ActiveBuffer.Lines.SetCells(line, startColumn, -1, CellStyle.Default);
        }
        EnsureCursorPosition(_cursor);
        if (!cellStyle.IsDefault)
        {
            int paddingLength = _view.VisibleColumns - startColumn;
            if (paddingLength > 0)
            {
                _view.Document.ReplaceInLine(textPosition.Line, textPosition.Column, -1, new string(' ', paddingLength));
                ActiveBuffer.Lines.SetCells(line, startColumn, paddingLength, cellStyle);
            }
        }
    }

    /// <summary>The text that follows is not the continuation of the text before it (a link or a diagnostic is not looked for across it).</summary>
    public void ResetLinkTracking()
    {
        _linkTracker?.Finish();
        _linkTracker = null;
    }

    /// <summary>
    /// Removes the scrollback and everything on the screen above the line of the cursor, which moves to the top together with its prompt.
    /// The alternate screen belongs to the program and is left alone.
    /// </summary>
    public void ClearOutput()
    {
        if (_activeBufferIndex != 0)
        {
            return;
        }
        ScreenBuffer screenBuffer = ActiveBuffer;
        int cursorLine = screenBuffer.FirstRow + _cursor.Y;
        // A command line that wrapped over several rows is one line for the shell, which redraws it as a whole.
        while (cursorLine > 0 && screenBuffer.Lines.GetLine(cursorLine - 1)?.IsWrapped == true)
        {
            cursorLine--;
        }
        int linesToRemove = Math.Min(cursorLine, screenBuffer.Document.LineCount);
        if (linesToRemove <= 0)
        {
            return;
        }
        int cursorRow = screenBuffer.FirstRow + _cursor.Y - linesToRemove;
        screenBuffer.Document.RemoveLines(0, linesToRemove);
        screenBuffer.Lines.RemoveLines(0, linesToRemove);
        screenBuffer.FirstRow = 0;
        _view.SetSelection(select: false, _view.SelectionStart);
        Cursor = new GridPoint(_cursor.X, Math.Max(cursorRow, 0));
        _view.TopLine = 0;
        _view.OnTextReplaced();
    }

    /// <summary>Removes the lines above the limit of the scrollback of the active buffer.</summary>
    public void TrimScrollback()
    {
        ScreenBuffer screenBuffer = ActiveBuffer;
        int overflowLines = screenBuffer.Document.LineCount - _view.VisibleRows;
        if (overflowLines > screenBuffer.MaxScrollback)
        {
            int linesToRemove = overflowLines - screenBuffer.MaxScrollback;
            screenBuffer.Document.RemoveLines(0, linesToRemove);
            screenBuffer.Lines.RemoveLines(0, linesToRemove);
            ActiveBuffer.FirstRow -= linesToRemove;
            if (ActiveBuffer.FirstRow < 0)
            {
                ActiveBuffer.FirstRow = 0;
            }
            _view.UpdateLayout();
            _view.Redraw();
        }
    }

    /// <summary>Empties the active buffer, puts the cursor at the top left and takes the style off.</summary>
    public void Reset()
    {
        _view.Document.Clear();
        ActiveBuffer.FirstRow = 0;
        Cursor = new GridPoint(0, 0);
        _view.SetSelection(select: false, new TextDocument.TextPosition(0, 0));
        ActiveBuffer.Lines.Clear();
        CurrentStyle = default;
        _view.OnTextReplaced();
    }

    /// <summary>
    /// Blanks the active screen buffer and its colours, keeping the cursor where it is. Used when an application
    /// switches to the alternate screen (modes 1047/1049), which must not show what the previous one left there.
    /// </summary>
    public void ClearActiveScreen()
    {
        _view.Document.Clear();
        ActiveBuffer.FirstRow = 0;
        ActiveBuffer.Lines.Clear();
        _view.SetSelection(select: false, new TextDocument.TextPosition(0, 0));
        EnsureCursorPosition(_cursor);
        _view.OnTextReplaced();
    }

    private void DeleteLines(int line, int count, bool scroll)
    {
        EditableTextDocument document = _view.Document;
        if (scroll)
        {
            int insertLine = Math.Min(document.LineCount, ActiveBuffer.FirstRow + _view.VisibleRows);
            if (_scrollRegion != null)
            {
                insertLine = Math.Min(insertLine, ActiveBuffer.FirstRow + _scrollRegion.Bottom + 1);
            }
            document.InsertText(new TextDocument.TextPosition(0, insertLine), new string('\n', count));
            ActiveBuffer.Lines.InsertLines(insertLine, count);
        }
        int documentLine = line + ActiveBuffer.FirstRow;
        int linesToRemove = Math.Min(count, document.LineCount - documentLine);
        document.RemoveLines(documentLine, linesToRemove);
        ActiveBuffer.Lines.RemoveLines(documentLine, linesToRemove);
        _view.OnTextReplaced();
    }

    public void ScrollRegionUp(int topLine, int count)
    {
        DeleteLines(topLine, count, scroll: true);
    }

    public void InsertBlankLines(int line, int count)
    {
        InsertBlankLines(line, count, redraw: true);
    }

    public void InsertBlankLines(int line, int count, bool redraw)
    {
        EditableTextDocument document = _view.Document;
        int blankInsertLine = _view.VisibleRows;
        if (_scrollRegion != null && _scrollRegion.Contains(line))
        {
            blankInsertLine = _scrollRegion.Bottom + 1;
        }
        int documentLine = line + ActiveBuffer.FirstRow;
        document.InsertText(new TextDocument.TextPosition(0, documentLine), new string('\n', count));
        ActiveBuffer.Lines.InsertLines(documentLine, count);
        if (redraw && document.LineCount > ActiveBuffer.FirstRow + blankInsertLine)
        {
            DeleteLines(blankInsertLine, count, scroll: false);
        }
        _view.OnTextReplaced();
    }

    /// <summary>The text of a screen row.</summary>
    public string GetLineText(int line)
    {
        TextDocument.TextPosition textPosition = ToDocumentPosition(new GridPoint(0, line));
        return _view.Document.GetLineText(textPosition.Line);
    }

    public void SetScrollRegion(int top, int bottom)
    {
        if ((top == 0 && bottom == _view.VisibleRows - 1) || bottom <= top)
        {
            ClearScrollRegion();
            return;
        }
        _scrollRegion = new ScrollRegion
        {
            Top = top,
            Bottom = bottom
        };
        // DECSTBM moves the cursor to the home position.
        Cursor = new GridPoint(0, 0);
        EnsureCursorPosition(_cursor);
    }

    public void ClearScrollRegion()
    {
        _scrollRegion = null;
        Cursor = new GridPoint(0, 0);
        EnsureCursorPosition(_cursor);
    }

    /// <summary>Puts what is on the screen in the scrollback, dimmed, and the cursor on a new row (a new session starts under it).</summary>
    public void ArchiveScreen()
    {
        ActiveBufferIndex = 0;
        ActiveBuffer.Lines.DimLines(ToDocumentPosition(_cursor).Line + 1);
        if (_cursor.X > 0)
        {
            MoveCursorBy(-_cursor.X, 1, scroll: true);
        }
        _view.Redraw();
    }
}
