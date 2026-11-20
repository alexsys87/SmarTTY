using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>What <see cref="TextViewLayout"/> reads of its view: the text and how it is shown, and the size of the view and of its scroll bars, in device pixels.</summary>
public interface ITextLayoutView
{
    TextDocument Document { get; }

    int TabSize { get; }

    /// <summary>Long lines are wrapped at the right edge instead of scrolling sideways.</summary>
    bool WordWrap { get; }

    /// <summary>The numbers of the lines are shown at the left.</summary>
    bool ShowLineNumbers { get; }

    int ClientWidth { get; }

    int ClientHeight { get; }

    int VerticalBarWidth { get; }

    int HorizontalBarHeight { get; }

    bool VerticalBarVisible { get; }

    bool HorizontalBarVisible { get; }

    /// <summary>The folded blocks of the text (null: the view does not fold).</summary>
    ITextFolds Folds { get; }

    /// <summary>The messages shown in rows of their own under their lines (null: the view shows none).</summary>
    ILineMessages Messages { get; }
}

/// <summary>Which lines have a message shown in a row of its own under them.</summary>
public interface ILineMessages
{
    bool HasMessage(int line);
}

/// <summary>The folded blocks as the layout sees them: the lines a folded header hides after it, and the text the header shows instead of its own.</summary>
public interface ITextFolds
{
    /// <summary>How many lines after <paramref name="line"/> its folded block hides (0: the line is not a folded header).</summary>
    int GetHiddenLineCount(int line);

    /// <summary>
    /// The text a folded header shows instead of <paramref name="lineText"/> and where its label is in it (an index and a length); null when the line
    /// is not a folded header.
    /// </summary>
    string GetFoldedText(int line, string lineText, out int labelStart, out int labelLength);
}

/// <summary>A part of a line drawn in one colour: from <see cref="From"/> to before <see cref="To"/>, in indexes of the line or in columns of a row.</summary>
public struct TextRun
{
    public int From;

    public int To;

    /// <summary>Which of the colours of the view (-1: the colour of the text, <see cref="TextViewLayout.FoldLabelColor"/>: the label of a folded header).</summary>
    public int Color;

    public TextRun(int from, int to, int color)
    {
        From = from;
        To = to;
        Color = color;
    }

    public override string ToString()
    {
        return From + "-" + To + ":" + Color;
    }
}

/// <summary>
/// The layout of a text view that draws on a grid of characters (stage 3D, for the WPF text view; the WinForms view has its own): the rows (a
/// line each, or the rows of <see cref="WrapMap"/> while the lines are wrapped), the margin of the line numbers, the rows and the width in view,
/// what a row shows (the tabs spread over their columns), the selected columns of a row, the position under a point and the point of a
/// position, the scroll that keeps the caret in view, which scroll bars are needed, the wheel and the automatic scrolling of a drag. It counts in
/// device pixels; the first row and the horizontal scroll are its state.
/// </summary>
public sealed class TextViewLayout : IWrapMapSource
{
    /// <summary>The blank between the margin (or the left edge) and the text, in pixels.</summary>
    public const int Margin = 4;

    /// <summary>The colour of a run that is the label of a folded header (drawn grey in a frame).</summary>
    public const int FoldLabelColor = -2;

    private readonly ITextLayoutView _view;

    private readonly WrapMap _wrap;

    // The widest line in columns (-1: to be counted again).
    private int _maxColumns = -1;

    private int _wheelRemainder;

    // The rows while blocks can be folded or messages shown, in order: a line, or a message under line n as -(n + 1) (null: to be built again).
    private int[] _rows;

    public TextViewLayout(ITextLayoutView view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _wrap = new WrapMap(this);
    }

    /// <summary>Where the lines break while they are wrapped.</summary>
    public WrapMap Wrap => _wrap;

    public int CharWidth { get; private set; } = 8;

    public int RowHeight { get; private set; } = 18;

    /// <summary>The height of the text in a row; it is centred in the row.</summary>
    public int TextHeight { get; private set; } = 16;

    /// <summary>The first row in view.</summary>
    public int FirstRow { get; set; }

    /// <summary>How far the text is scrolled to the left, in pixels (0 while the lines are wrapped).</summary>
    public int ScrollX { get; set; }

    /// <summary>The size of a character cell and of the text, measured from the font.</summary>
    public void SetMetrics(int charWidth, int textHeight)
    {
        CharWidth = Math.Max(1, charWidth);
        TextHeight = Math.Max(1, textHeight);
        RowHeight = TextHeight + 2;
    }

    private TextDocument Document => _view.Document;

    private bool Wrapping => _view.WordWrap;

    // Folds hide lines only while the lines are not wrapped (as in the WinForms view).
    private bool Folding => !Wrapping && _view.Folds != null;

    // The rows are a list of their own while lines can be hidden or messages shown, which the wrapped rows do not have (as in the WinForms view).
    private bool HasRowList => !Wrapping && (_view.Folds != null || _view.Messages != null);

    private static int LineOfRow(int entry)
    {
        return entry >= 0 ? entry : -entry - 1;
    }

    /// <summary>The width of the margin of the fold marks, after the numbers, in pixels (it is there when the view folds).</summary>
    public int FoldMarginWidth { get; set; }

    /// <summary>The folds, the messages or the number of the lines changed: the rows are built again when they are asked for.</summary>
    public void InvalidateRows()
    {
        _rows = null;
    }

    private int[] Rows
    {
        get
        {
            if (_rows == null)
            {
                List<int> rows = new List<int>();
                int lineCount = Document.LineCount;
                ITextFolds folds = Folding ? _view.Folds : null;
                ILineMessages messages = _view.Messages;
                for (int line = 0; line < lineCount; line += 1 + (folds == null ? 0 : Math.Max(0, folds.GetHiddenLineCount(line))))
                {
                    rows.Add(line);
                    if (messages != null && messages.HasMessage(line))
                    {
                        rows.Add(-line - 1);
                    }
                }
                _rows = rows.ToArray();
            }
            return _rows;
        }
    }

    /// <summary>The row shows the message of its line (the line above it), not text.</summary>
    public bool IsMessageRow(int row)
    {
        return HasRowList && row >= 0 && row < Rows.Length && Rows[row] < 0;
    }

    /// <summary>The text a line shows: the text of its fold when it is a folded header, else its own.</summary>
    public string LineText(int line)
    {
        string text = Document.GetLineText(line);
        if (!Folding)
        {
            return text;
        }
        return _view.Folds.GetFoldedText(line, text, out _, out _) ?? text;
    }

    /// <summary>The display column of an index of a text (a tab goes to the next tab stop; an index past the end is the column after the text).</summary>
    public static int IndexToColumn(string text, int index, int tabSize)
    {
        int column = 0;
        for (int i = 0; i < text.Length && i < index; i++)
        {
            column = (text[i] != '\t') ? (column + 1) : (column + (tabSize - column % tabSize));
        }
        return column;
    }

    /// <summary>The index of a display column of a text (a column inside a tab is the tab; a column past the end is the end), as TextDocument counts.</summary>
    public static int ColumnToIndex(string text, int column, int tabSize)
    {
        int currentColumn = 0;
        int index = 0;
        while (true)
        {
            if (currentColumn == column)
            {
                return index;
            }
            if (currentColumn > column)
            {
                return index - 1;
            }
            if (index >= text.Length)
            {
                return index;
            }
            currentColumn = (text[index] != '\t') ? (currentColumn + 1) : (currentColumn + (tabSize - currentColumn % tabSize));
            index++;
        }
    }

    // The display column of an index of the text a line shows.
    private int ColumnOf(int line, int index)
    {
        return IndexToColumn(LineText(line), index, TabSize);
    }

    /// <summary>The line shown before <paramref name="line"/> (the line itself at the top), past the lines a fold hides.</summary>
    public int LineBefore(int line)
    {
        if (!Folding)
        {
            return Math.Max(0, line - 1);
        }
        int row = PositionToRow(new TextDocument.TextPosition(0, line));
        return row > 0 ? LineOfRow(Rows[row - 1]) : line;
    }

    /// <summary>The line shown after <paramref name="line"/> (the line itself at the end), past the lines a fold hides.</summary>
    public int LineAfter(int line)
    {
        if (!Folding)
        {
            return Math.Min(Document.LineCount - 1, line + 1);
        }
        int row = PositionToRow(new TextDocument.TextPosition(0, line)) + 1;
        while (row < Rows.Length && Rows[row] < 0)
        {
            row++;
        }
        return row < Rows.Length ? Rows[row] : line;
    }

    /// <summary>A point is on the margin of the fold marks.</summary>
    public bool IsInFoldMargin(int x)
    {
        int left = NumbersWidth;
        return _view.Folds != null && x >= left && x < left + FoldMarginWidth;
    }

    // ---- IWrapMapSource

    int IWrapMapSource.LineCount => Document.LineCount;

    string IWrapMapSource.GetLineText(int line)
    {
        return Document.GetLineText(line);
    }

    int IWrapMapSource.TabSize => TabSize;

    private int TabSize => Math.Max(1, _view.TabSize);

    /// <summary>
    /// The columns the lines are wrapped to: the width of the text area with room for the vertical scroll bar always kept, so that showing the bar
    /// does not wrap the lines again.
    /// </summary>
    public int WrapWidth => Math.Max(2, (_view.ClientWidth - _view.VerticalBarWidth - TextLeft) / CharWidth);

    // ---- the margin and the text area

    /// <summary>The width of the line numbers (0 when they are not shown).</summary>
    public int NumbersWidth
    {
        get
        {
            if (!_view.ShowLineNumbers)
            {
                return 0;
            }
            int digits = Math.Max(2, Document.LineCount.ToString(CultureInfo.InvariantCulture).Length);
            return Margin + (digits + 1) * CharWidth;
        }
    }

    /// <summary>The width of the margin: the line numbers and the fold marks.</summary>
    public int GutterWidth => NumbersWidth + (_view.Folds != null ? FoldMarginWidth : 0);

    /// <summary>Where the text starts, before the horizontal scroll.</summary>
    public int TextLeft => GutterWidth + Margin;

    /// <summary>The width that is not under the vertical scroll bar.</summary>
    public int VisibleWidth => Math.Max(1, _view.ClientWidth - (_view.VerticalBarVisible ? _view.VerticalBarWidth : 0));

    /// <summary>The height that is not under the horizontal scroll bar.</summary>
    public int VisibleHeight => Math.Max(1, _view.ClientHeight - (_view.HorizontalBarVisible ? _view.HorizontalBarHeight : 0));

    /// <summary>The rows that fit whole in view (at least one).</summary>
    public int VisibleRows => Math.Max(1, VisibleHeight / RowHeight);

    /// <summary>All rows.</summary>
    public int RowCount => Wrapping ? _wrap.RowCount : (HasRowList ? Rows.Length : Document.LineCount);

    /// <summary>The columns of the text a line shows.</summary>
    public int LineColumns(int line)
    {
        string text = LineText(line);
        return IndexToColumn(text, text.Length, TabSize);
    }

    /// <summary>The widest line in columns (counted again after <see cref="UpdateMaxColumns"/> with -1).</summary>
    public int MaxColumns
    {
        get
        {
            if (_maxColumns < 0)
            {
                int max = 0;
                for (int line = 0; line < Document.LineCount; line++)
                {
                    max = Math.Max(max, LineColumns(line));
                }
                _maxColumns = max;
            }
            return _maxColumns;
        }
    }

    /// <summary>The view was given another document: the widest line is counted again and the lines are wrapped again.</summary>
    public void OnDocumentChanged()
    {
        _maxColumns = -1;
        _rows = null;
        _wrap.Invalidate();
    }

    /// <summary>A line changed: the widest line may now be wider; -1: anything may have changed (the widest is counted again when asked).</summary>
    public void UpdateMaxColumns(int line)
    {
        if (line < 0 || line >= Document.LineCount)
        {
            _maxColumns = -1;
            return;
        }
        if (_maxColumns >= 0)
        {
            _maxColumns = Math.Max(_maxColumns, LineColumns(line));
        }
    }

    /// <summary>The width of the text with the margin and one cell for the caret after the widest line, in pixels.</summary>
    public int ContentWidth => TextLeft + (MaxColumns + 1) * CharWidth;

    // ---- rows

    /// <summary>The line a row shows and the display columns of the row: from <paramref name="startColumn"/> to before <paramref name="endColumn"/>.</summary>
    public int RowToLine(int row, out int startColumn, out int endColumn)
    {
        int lineCount = Document.LineCount;
        if (!Wrapping)
        {
            // The row of a message has the columns of its line, so that a point on it is a point on its line.
            int line = HasRowList ? LineOfRow(Rows[Math.Max(0, Math.Min(row, Rows.Length - 1))]) : Math.Max(0, Math.Min(row, lineCount - 1));
            startColumn = 0;
            endColumn = LineColumns(line);
            return line;
        }
        int wrappedLine = _wrap.RowIndexToLine(row, out int segment);
        startColumn = _wrap.SegmentStart(wrappedLine, segment);
        int end = _wrap.SegmentEnd(wrappedLine, segment);
        endColumn = (end == int.MaxValue) ? LineColumns(wrappedLine) : end;
        return wrappedLine;
    }

    /// <summary>The row a row shows is the first of its line (the one that has the number of the line).</summary>
    public bool IsFirstRowOfLine(int row)
    {
        if (!Wrapping)
        {
            return !IsMessageRow(row);
        }
        _wrap.RowIndexToLine(row, out int segment);
        return segment == 0;
    }

    /// <summary>The row that shows a position (the start of a row that the line was wrapped at is in that row).</summary>
    public int PositionToRow(TextDocument.TextPosition position)
    {
        int line = Math.Max(0, Math.Min(position.Line, Document.LineCount - 1));
        if (!Wrapping)
        {
            if (!HasRowList)
            {
                return line;
            }
            // A hidden line is in the row of its header: the last line shown that is not after it (not the row of its message).
            int[] rows = Rows;
            int low = 0;
            int high = rows.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (LineOfRow(rows[middle]) <= line)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return (low > 0 && rows[low] < 0) ? low - 1 : low;
        }
        return _wrap.PositionToRowIndex(line, Document.IndexToColumn(line, position.Column, TabSize));
    }

    /// <summary>The text of a row as it is drawn, one character a column: the tabs are spread over their columns as blanks.</summary>
    public string RowText(int row)
    {
        if (IsMessageRow(row))
        {
            return "";
        }
        int line = RowToLine(row, out int startColumn, out int endColumn);
        string text = LineText(line);
        StringBuilder result = new StringBuilder(Math.Max(0, endColumn - startColumn));
        int column = 0;
        int tabSize = TabSize;
        foreach (char ch in text)
        {
            if (column >= endColumn)
            {
                break;
            }
            int width = (ch == '\t') ? (tabSize - column % tabSize) : 1;
            for (int i = 0; i < width; i++)
            {
                if (column + i >= startColumn && column + i < endColumn)
                {
                    result.Append(ch == '\t' ? ' ' : ch);
                }
            }
            column += width;
        }
        return result.ToString();
    }

    /// <summary>
    /// The colours of a row: the parts of its line that <paramref name="lineRuns"/> colours (in indexes of the line, in the order of the line; what
    /// one leaves out, or what goes back over an earlier one, is skipped), turned into columns of the row (a tab takes its columns) and cut to the
    /// row, with the rest of the row in the colour of the text (-1), and neighbours of one colour joined. The runs cover the row from 0 to its width.
    /// The row of a folded header has the colours up to its label, the label (<see cref="FoldLabelColor"/>), and the rest in the colour of the text.
    /// </summary>
    public List<TextRun> RowRuns(int row, IList<TextRun> lineRuns)
    {
        int line = RowToLine(row, out int startColumn, out int endColumn);
        int width = endColumn - startColumn;
        List<TextRun> runs = new List<TextRun>();
        if (IsMessageRow(row))
        {
            return runs;
        }
        int column = 0;
        int labelStart = int.MaxValue;
        int labelLength = 0;
        string text = Document.GetLineText(line);
        if (Folding && _view.Folds.GetFoldedText(line, text, out labelStart, out labelLength) != null)
        {
            text = LineText(line);
        }
        else
        {
            labelStart = int.MaxValue;
        }
        if (lineRuns != null)
        {
            foreach (TextRun run in lineRuns)
            {
                int from = IndexToColumn(text, run.From, TabSize) - startColumn;
                int to = IndexToColumn(text, Math.Min(run.To, labelStart), TabSize) - startColumn;
                from = Math.Max(from, column);
                to = Math.Min(to, width);
                if (to <= from)
                {
                    continue;
                }
                Add(runs, column, from, -1);
                Add(runs, from, to, run.Color);
                column = to;
            }
        }
        if (labelStart != int.MaxValue)
        {
            int labelFrom = Math.Max(column, IndexToColumn(text, labelStart, TabSize) - startColumn);
            int labelTo = Math.Min(width, IndexToColumn(text, labelStart + labelLength, TabSize) - startColumn);
            Add(runs, column, labelFrom, -1);
            Add(runs, labelFrom, labelTo, FoldLabelColor);
            column = Math.Max(column, labelTo);
        }
        Add(runs, column, width, -1);
        return runs;
    }

    // Adds a run that is not empty, joined to the last one when it has the same colour.
    private static void Add(List<TextRun> runs, int from, int to, int color)
    {
        if (to <= from)
        {
            return;
        }
        if (runs.Count > 0 && runs[runs.Count - 1].Color == color && runs[runs.Count - 1].To == from)
        {
            TextRun last = runs[runs.Count - 1];
            last.To = to;
            runs[runs.Count - 1] = last;
            return;
        }
        runs.Add(new TextRun(from, to, color));
    }

    /// <summary>
    /// The columns of a row that a selection from <paramref name="start"/> to <paramref name="end"/> (in either order) covers, counted from the
    /// start of the row; <paramref name="pastEnd"/>: the selection goes on past the end of the line, in the last row of the line. False when it
    /// covers nothing of the row.
    /// </summary>
    public bool SelectedColumns(int row, TextDocument.TextPosition start, TextDocument.TextPosition end, out int from, out int to, out bool pastEnd)
    {
        from = 0;
        to = 0;
        pastEnd = false;
        if (end < start)
        {
            TextDocument.TextPosition swap = start;
            start = end;
            end = swap;
        }
        int line = RowToLine(row, out int startColumn, out int endColumn);
        if (line < start.Line || line > end.Line || IsMessageRow(row))
        {
            return false;
        }
        int lineColumns = LineColumns(line);
        int first = (line == start.Line) ? ColumnOf(line, start.Column) : 0;
        int last = (line == end.Line) ? ColumnOf(line, end.Column) : lineColumns;
        bool lastRowOfLine = endColumn >= lineColumns;
        pastEnd = line < end.Line && lastRowOfLine;
        from = Math.Max(first, startColumn) - startColumn;
        to = Math.Min(last, endColumn) - startColumn;
        if (to < from)
        {
            to = from;
        }
        return to > from || pastEnd;
    }

    /// <summary>The number of a line as the margin shows it, for the first row of a line; null for the other rows.</summary>
    public string LineNumberText(int row)
    {
        if (!IsFirstRowOfLine(row) || row >= RowCount)
        {
            return null;
        }
        int line = RowToLine(row, out _, out _);
        return (line + 1).ToString(CultureInfo.InvariantCulture);
    }

    // ---- points

    /// <summary>
    /// The position under a point of the view (the nearer edge of the character under it, at most the end of its row); a point above or below the
    /// rows goes to the first or the last row, one left of the text to the start of the row.
    /// </summary>
    public TextDocument.TextPosition HitTest(int x, int y)
    {
        int rowCount = RowCount;
        int row = FirstRow + (int)Math.Floor((double)y / RowHeight);
        row = Math.Max(0, Math.Min(row, rowCount - 1));
        int line = RowToLine(row, out int startColumn, out int endColumn);
        int offset = x - TextLeft + (Wrapping ? 0 : ScrollX);
        int column = startColumn + (int)Math.Floor((offset + CharWidth / 2.0) / CharWidth);
        column = Math.Max(startColumn, Math.Min(column, endColumn));
        string text = LineText(line);
        int index = ColumnToIndex(text, column, TabSize);
        int length = text.Length;
        return new TextDocument.TextPosition(Math.Max(0, Math.Min(index, length)), line);
    }

    /// <summary>The point of a position in the view: the left edge of its cell and the top of its row (rows above the view have a negative y).</summary>
    public void PositionToPoint(TextDocument.TextPosition position, out int x, out int y)
    {
        int row = PositionToRow(position);
        int line = Math.Max(0, Math.Min(position.Line, Document.LineCount - 1));
        if (Folding && RowToLine(row, out _, out _) != line)
        {
            // A hidden line: the start of the row of its header.
            x = TextLeft - ScrollX;
            y = (row - FirstRow) * RowHeight;
            return;
        }
        int column = ColumnOf(line, position.Column);
        int startColumn = 0;
        if (Wrapping)
        {
            RowToLine(row, out startColumn, out _);
        }
        x = TextLeft + (column - startColumn) * CharWidth - (Wrapping ? 0 : ScrollX);
        y = (row - FirstRow) * RowHeight;
    }

    // ---- scrolling

    /// <summary>A first row brought into the rows: not before the first, not past the one that shows the last row at the bottom.</summary>
    public int ClampFirstRow(int row)
    {
        return Math.Max(0, Math.Min(row, Math.Max(0, RowCount - VisibleRows)));
    }

    /// <summary>The horizontal scroll brought into the content (0 while the lines are wrapped).</summary>
    public int ClampScrollX(int scrollX)
    {
        return Wrapping ? 0 : HexScroll.ClampScrollX(scrollX, ContentWidth, VisibleWidth);
    }

    /// <summary>Scrolls so that the position is in view; true when the view moved.</summary>
    public bool EnsureVisible(TextDocument.TextPosition position)
    {
        int firstRow = FirstRow;
        int scrollX = ScrollX;
        int row = PositionToRow(position);
        int visibleRows = VisibleRows;
        if (row < FirstRow)
        {
            FirstRow = row;
        }
        else if (row >= FirstRow + visibleRows)
        {
            FirstRow = row - visibleRows + 1;
        }
        FirstRow = ClampFirstRow(FirstRow);
        if (Wrapping)
        {
            ScrollX = 0;
        }
        else
        {
            int line = Math.Max(0, Math.Min(position.Line, Document.LineCount - 1));
            int x = ColumnOf(line, position.Column) * CharWidth;
            int width = Math.Max(CharWidth, VisibleWidth - TextLeft);
            if (x < ScrollX)
            {
                // A few columns before the caret stay in view when it goes left beyond the edge.
                ScrollX = Math.Max(0, x - 4 * CharWidth);
            }
            else if (x + CharWidth > ScrollX + width)
            {
                ScrollX = x + CharWidth - width;
            }
        }
        return firstRow != FirstRow || scrollX != ScrollX;
    }

    /// <summary>Which scroll bars the view needs: the vertical one for more rows than fit, the horizontal one for a line wider than the view (never while wrapping).</summary>
    public void ChooseScrollBars(out bool needVertical, out bool needHorizontal)
    {
        int clientWidth = _view.ClientWidth;
        int clientHeight = _view.ClientHeight;
        int rowCount = RowCount;
        int contentWidth = Wrapping ? 0 : ContentWidth;
        needVertical = rowCount > Math.Max(1, clientHeight / RowHeight);
        needHorizontal = !Wrapping && contentWidth > clientWidth - (needVertical ? _view.VerticalBarWidth : 0);
        if (needHorizontal && !needVertical)
        {
            // The horizontal bar takes a row: the vertical one may be needed now, and it takes width.
            needVertical = rowCount > Math.Max(1, (clientHeight - _view.HorizontalBarHeight) / RowHeight);
        }
    }

    /// <summary>The rows to scroll by a wheel delta (three a notch; a touchpad's small steps are added up, the rest is kept).</summary>
    public int WheelRows(int delta)
    {
        return HexScroll.WheelNotches(ref _wheelRemainder, delta) * 3;
    }

    /// <summary>
    /// The scroll of a timer tick while a selection is dragged beyond the edge of the text: rows up (negative) or down above or below the rows in
    /// view, pixels left or right beyond the text area (none while wrapping).
    /// </summary>
    public void AutoScrollStep(int x, int y, out int rows, out int pixels)
    {
        rows = 0;
        pixels = 0;
        int bottom = VisibleRows * RowHeight;
        if (y < 0)
        {
            rows = -HexScroll.AutoScrollSteps(-y, RowHeight);
        }
        else if (y >= bottom)
        {
            rows = HexScroll.AutoScrollSteps(y - bottom + 1, RowHeight);
        }
        if (Wrapping)
        {
            return;
        }
        int left = TextLeft;
        int right = VisibleWidth;
        if (x < left)
        {
            pixels = -HexScroll.AutoScrollSteps(left - x, CharWidth) * CharWidth;
        }
        else if (x >= right)
        {
            pixels = HexScroll.AutoScrollSteps(x - right + 1, CharWidth) * CharWidth;
        }
    }
}
