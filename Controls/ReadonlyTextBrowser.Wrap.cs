using System.ComponentModel;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Word wrap. The scroll position (ScrollLine, the vertical scroll bar) counts display rows instead of lines while
// the text is wrapped: a long line takes several rows, and every row shows the columns [SegStart, SegEnd) of its line.
// Folded blocks and virtual lines are not shown meanwhile. Where the lines break is CodeEditing/WrapMap (stage 3C);
// this partial keeps the width, the rows on the screen and the scrolling.
public partial class ReadonlyTextBrowser : IWrapMapSource
{
    private bool _wordWrap;

    // The width in columns the lines are wrapped to; set by UpdateLayout.
    private int _wrapWidth = 80;

    private bool _wrapRefreshPending;

    // Where the lines break (made when it is first used: the base classes may ask before the constructor has run).
    private WrapMap _wrap;

    // While a row is painted: the display column its left edge shows, and the last column that belongs to it (-1: none).
    private int _rowOrigin = -1;

    private int _rowLastColumn = -1;

    /// <summary>Where the lines break while word wrap is on.</summary>
    internal WrapMap WrapLayout => _wrap ?? (_wrap = new WrapMap(this));

    int IWrapMapSource.LineCount => _document.LineCount;

    string IWrapMapSource.GetLineText(int line)
    {
        return _document.GetLineText(line);
    }

    int IWrapMapSource.WrapWidth => _wrapWidth;

    int IWrapMapSource.TabSize => TabSize;

    /// <summary>Long lines continue on the next row instead of scrolling sideways.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
            WrapLayout.Invalidate();
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
                ScrollLine = WrapLayout.FirstRows[Math.Min(topLine, _document.LineCount)];
            }
            else
            {
                _horizontalScrollBar.Value = 0;
                UpdateLayout();
                ScrollLine = topLine;
            }
            UpdateLayout();
            Redraw();
            OnWordWrapChanged();
        }
    }

    /// <summary>The text is now wrapped (or no longer is): the rows changed and whatever sits between them may not fit.</summary>
    protected virtual void OnWordWrapChanged()
    {
    }

    internal bool IsWrapping => _wordWrap && !_singleLine && !_terminalMode;

    private int ScrollRowCount
    {
        get
        {
            if (IsWrapping)
            {
                return WrapLayout.RowCount;
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
        WrapLayout.Invalidate();
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
        WrapLayout.Ensure();
    }

    /// <summary>The line and the row of that line (0 for the first) shown at a row index.</summary>
    private int RowIndexToLine(int rowIndex, out int segment)
    {
        return WrapLayout.RowIndexToLine(rowIndex, out segment);
    }

    /// <summary>The row of a line (0 for the first) that shows a display column.</summary>
    private int WrapSegmentOf(int line, int displayColumn)
    {
        return WrapLayout.SegmentOf(line, displayColumn);
    }

    private int WrapSegmentStart(int line, int segment)
    {
        return WrapLayout.SegmentStart(line, segment);
    }

    private int WrapSegmentEnd(int line, int segment)
    {
        return WrapLayout.SegmentEnd(line, segment);
    }

    private int PositionToRowIndex(int line, int displayColumn)
    {
        return WrapLayout.PositionToRowIndex(line, displayColumn);
    }

    private void RebuildWrappedRows()
    {
        EnsureWrapMap();
        int[][] breaks = WrapLayout.Breaks;
        int rowCount = WrapLayout.FirstRows[WrapLayout.FirstRows.Length - 1];
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
            if (breaks[line] == null || segment > breaks[line].Length)
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
