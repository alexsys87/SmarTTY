using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Word wrap. The scroll position (ScrollLine, the vertical scroll bar) counts display rows instead of lines while
// the text is wrapped: a long line takes several rows, and every row shows the columns [SegStart, SegEnd) of its line.
// Folded blocks and virtual lines are not shown meanwhile.
public partial class ReadonlyTextBrowser
{
    private bool _wordWrap;

    // The width in columns the lines are wrapped to; set by UpdateLayout.
    private int _wrapWidth = 80;

    private int _wrapBuiltWidth = -1;

    private int _wrapBuiltTabSize = -1;

    private bool _wrapDirty = true;

    private bool _wrapRefreshPending;

    // Per line: the display columns where the rows after the first one start (null: the line takes one row).
    private int[][] _wrapBreaks = new int[0][];

    // Per line: the index of its first row; one more entry holds the number of all rows.
    private int[] _wrapFirstRow = new int[] { 0 };

    // While a row is painted: the display column its left edge shows, and the last column that belongs to it (-1: none).
    private int _rowOrigin = -1;

    private int _rowLastColumn = -1;

    // Where the caret column sits inside its row, kept while the caret moves up and down.
    private int _desiredWrapOffset;

    /// <summary>Long lines continue on the next row instead of scrolling sideways.</summary>
    public bool WordWrap
    {
        get
        {
            return _wordWrap;
        }
        set
        {
            if (_wordWrap == value)
            {
                return;
            }
            bool wasWrapping = IsWrapping;
            int topLine = wasWrapping ? RowIndexToLine(ScrollLine, out _) : ScrollLine;
            _wordWrap = value;
            _wrapDirty = true;
            if (_verticalScrollBar == null || _horizontalScrollBar == null)
            {
                return;
            }
            ScrollColumn = 0;
            if (IsWrapping)
            {
                // Limits first: the scroll bar clamps the value to them.
                UpdateLayout();
                EnsureWrapMap();
                ScrollLine = _wrapFirstRow[Math.Min(topLine, _document.LineCount)];
            }
            else
            {
                _horizontalScrollBar.Value = 0;
                UpdateLayout();
                ScrollLine = topLine;
            }
            UpdateLayout();
            Redraw();
        }
    }

    internal bool IsWrapping => _wordWrap && !_singleLine && !_terminalMode;

    private int ScrollRowCount
    {
        get
        {
            if (IsWrapping)
            {
                EnsureWrapMap();
                return _wrapFirstRow[_wrapFirstRow.Length - 1];
            }
            return LineCount;
        }
    }

    /// <summary>The text changed: the rows must be counted again before they are used.</summary>
    internal void InvalidateWrapMap()
    {
        if (!_wordWrap)
        {
            return;
        }
        _wrapDirty = true;
        if (_wrapRefreshPending || !IsHandleCreated)
        {
            return;
        }
        // Edits come in runs (Replace All): the scroll bar is updated once, after the run.
        _wrapRefreshPending = true;
        BeginInvoke((MethodInvoker)(() =>
        {
            _wrapRefreshPending = false;
            if (IsWrapping && !IsDisposed)
            {
                UpdateLayout();
                Redraw();
            }
        }));
    }

    private void EnsureWrapMap()
    {
        int lineCount = _document.LineCount;
        if (!_wrapDirty && _wrapBuiltWidth == _wrapWidth && _wrapBuiltTabSize == TabSize && _wrapBreaks.Length == lineCount)
        {
            return;
        }
        int[][] breaks = new int[lineCount][];
        int[] firstRow = new int[lineCount + 1];
        int rows = 0;
        for (int line = 0; line < lineCount; line++)
        {
            firstRow[line] = rows;
            int[] lineBreaks = LineWrapper.ComputeBreaks(_document.GetLineText(line), _wrapWidth, TabSize);
            breaks[line] = lineBreaks;
            rows += 1 + (lineBreaks == null ? 0 : lineBreaks.Length);
        }
        firstRow[lineCount] = rows;
        _wrapBreaks = breaks;
        _wrapFirstRow = firstRow;
        _wrapBuiltWidth = _wrapWidth;
        _wrapBuiltTabSize = TabSize;
        _wrapDirty = false;
    }

    /// <summary>The line and the row of that line (0 for the first) shown at a row index.</summary>
    private int RowIndexToLine(int rowIndex, out int segment)
    {
        EnsureWrapMap();
        int lineCount = _document.LineCount;
        if (lineCount == 0 || rowIndex <= 0)
        {
            segment = 0;
            return 0;
        }
        int low = 0;
        int high = lineCount - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (_wrapFirstRow[middle] <= rowIndex)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        segment = Math.Min(rowIndex - _wrapFirstRow[low], _wrapBreaks[low] == null ? 0 : _wrapBreaks[low].Length);
        return low;
    }

    /// <summary>The row of a line (0 for the first) that shows a display column.</summary>
    private int WrapSegmentOf(int line, int displayColumn)
    {
        EnsureWrapMap();
        if (line < 0 || line >= _wrapBreaks.Length || _wrapBreaks[line] == null)
        {
            return 0;
        }
        int[] lineBreaks = _wrapBreaks[line];
        int segment = 0;
        while (segment < lineBreaks.Length && lineBreaks[segment] <= displayColumn)
        {
            segment++;
        }
        return segment;
    }

    private int WrapSegmentStart(int line, int segment)
    {
        return (segment == 0 || _wrapBreaks[line] == null) ? 0 : _wrapBreaks[line][segment - 1];
    }

    private int WrapSegmentEnd(int line, int segment)
    {
        int[] lineBreaks = _wrapBreaks[line];
        return (lineBreaks == null || segment >= lineBreaks.Length) ? int.MaxValue : lineBreaks[segment];
    }

    private int PositionToRowIndex(int line, int displayColumn)
    {
        EnsureWrapMap();
        line = Math.Max(0, Math.Min(line, _wrapBreaks.Length - 1));
        return _wrapFirstRow[line] + WrapSegmentOf(line, displayColumn);
    }

    private void RebuildWrappedRows()
    {
        EnsureWrapMap();
        int rowCount = _wrapFirstRow[_wrapFirstRow.Length - 1];
        int top = Math.Min(ScrollLine, Math.Max(rowCount - 1, 0));
        int line = RowIndexToLine(top, out int segment);
        _firstVisibleLine = line;
        _lastVisibleLine = line;
        for (int row = 0; row < _visibleRows; row++)
        {
            if (top + row >= rowCount || _document.LineCount == 0)
            {
                _displayRows[row].Line = -1;
                _displayRows[row].VirtualLine = null;
                _displayRows[row].Decorator = null;
                _displayRows[row].SegStart = 0;
                _displayRows[row].SegEnd = int.MaxValue;
                _displayRows[row].Continuation = false;
                continue;
            }
            _displayRows[row].Line = line;
            _displayRows[row].VirtualLine = null;
            _displayRows[row].Decorator = null;
            _displayRows[row].SegStart = WrapSegmentStart(line, segment);
            _displayRows[row].SegEnd = WrapSegmentEnd(line, segment);
            _displayRows[row].Continuation = segment > 0;
            _lastVisibleLine = line;
            segment++;
            if (_wrapBreaks[line] == null || segment > _wrapBreaks[line].Length)
            {
                line++;
                segment = 0;
            }
        }
        _previousRealLine = Math.Max(_firstVisibleLine - 1, 0);
        _nextRealLine = Math.Min(_lastVisibleLine + 1, Math.Max(_document.LineCount - 1, 0));
    }

    /// <summary>The width in columns the lines wrap to: the text area as if the vertical scroll bar were always shown.</summary>
    private int ComputeWrapWidth(int textAreaWidth)
    {
        int reservedForScrollBar = _verticalScrollBar.Visible ? 0 : SystemInformation.VerticalScrollBarWidth;
        return Math.Max(2, (int)Math.Floor((float)(textAreaWidth - reservedForScrollBar) / _charSize.Width));
    }

    /// <summary>The vertical position and the column origin of a position in wrap mode; false when its row is not on screen.</summary>
    private bool TryGetWrappedPoint(int displayColumn, int line, out float x, out float y)
    {
        x = 0f;
        y = -1f;
        int rowIndex = PositionToRowIndex(line, displayColumn);
        int row = rowIndex - ScrollLine;
        if (row < 0 || row >= _visibleRows)
        {
            return false;
        }
        int segmentStart = WrapSegmentStart(line, WrapSegmentOf(line, displayColumn));
        x = ColumnToXFrom(displayColumn, segmentStart);
        y = RowToY(row);
        return true;
    }

    /// <summary>Moves one display row up or down, keeping the caret column inside the row.</summary>
    private TextDocument.TextPosition MoveWrappedRow(TextDocument.TextPosition textPosition, int direction)
    {
        int column = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
        int rowIndex = PositionToRowIndex(textPosition.Line, column);
        int target = rowIndex + direction;
        if (target < 0 || target >= ScrollRowCount)
        {
            return textPosition;
        }
        int offset = _desiredColumn == -1 ? column - WrapSegmentStart(textPosition.Line, WrapSegmentOf(textPosition.Line, column)) : _desiredWrapOffset;
        int line = RowIndexToLine(target, out int segment);
        int targetColumn = WrapSegmentStart(line, segment) + offset;
        int segmentEnd = WrapSegmentEnd(line, segment);
        if (segmentEnd != int.MaxValue)
        {
            targetColumn = Math.Min(targetColumn, segmentEnd - 1);
        }
        int index = _document.ColumnToIndex(line, targetColumn, TabSize);
        textPosition.Line = line;
        textPosition.Column = Math.Min(index, _document.GetLineText(line).Length);
        return textPosition;
    }

    private bool EnsureWrappedVisible(TextDocument.TextPosition textPosition, bool center)
    {
        int column = _document.IndexToColumn(textPosition.Line, textPosition.Column, TabSize);
        int rowIndex = PositionToRowIndex(textPosition.Line, column);
        int top = ScrollLine;
        if (center)
        {
            TopLine = Math.Max(rowIndex - _visibleRows / 2, 0);
            return true;
        }
        if (rowIndex < top)
        {
            TopLine = rowIndex;
            return true;
        }
        if (rowIndex >= top + _visibleRows)
        {
            TopLine = rowIndex - _visibleRows + 1;
            return true;
        }
        return false;
    }
}
