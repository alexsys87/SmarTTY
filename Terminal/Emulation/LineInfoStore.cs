using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.Terminal.Emulation;

public class LineInfoStore
{
    private ILineInfoHost _host;

    private List<LineInfo> _lines = new List<LineInfo>();

    public LineInfoStore(ILineInfoHost host)
    {
        _host = host;
    }

    public LineInfo GetLine(int line)
    {
        if (line >= _lines.Count)
        {
            return null;
        }
        return _lines[line];
    }

    internal void SetCells(int row, int startColumn, int count, CellStyle cellStyle, LinkCell[] items = null)
    {
        TextDocument.TextPosition textPosition = this._host.ToDocumentPosition(new GridPoint(0, row));
        while (textPosition.Line >= this._lines.Count)
        {
            if (cellStyle.IsDefault && items == null && !this._host.AlwaysTrackLines && this._host.HideMargins)
            {
                return;
            }
            this._lines.Add(null);
        }
        if (this._lines[textPosition.Line] == null)
        {
            if (cellStyle.IsDefault && items == null && !this._host.AlwaysTrackLines && this._host.HideMargins)
            {
                return;
            }
            this._lines[textPosition.Line] = new LineInfo();
        }
        LineInfo lineInfo = this._lines[textPosition.Line];
        lineInfo.IsDimmed = false;
        lineInfo.IsWrapped = false;
        lineInfo.Timestamp = DateTime.Now;
        if (lineInfo.Cells == null || lineInfo.Cells.Length < this._host.VisibleColumns)
        {
            CellStyle[] cellStyles = new CellStyle[this._host.VisibleColumns];
            if (lineInfo.Cells != null)
            {
                lineInfo.Cells.CopyTo(cellStyles, 0);
            }
            lineInfo.Cells = cellStyles;
        }
        if (startColumn == -1 || startColumn > this._host.VisibleColumns)
        {
            startColumn = this._host.VisibleColumns;
        }
        int remainingColumns = this._host.VisibleColumns - startColumn;
        if (count == -1 || count > remainingColumns)
        {
            count = remainingColumns;
        }
        for (int cellOffset = 0; cellOffset < count; cellOffset++)
        {
            lineInfo.Cells[startColumn + cellOffset] = cellStyle;
            if (items != null && cellOffset < items.Length)
            {
                lineInfo.Cells[startColumn + cellOffset]._link = items[cellOffset];
            }
            else
            {
                lineInfo.Cells[startColumn + cellOffset]._link = default;
            }
        }
        // A cell written again loses the marks of the character it held.
        ClearMarks(lineInfo, startColumn, count);
    }

    /// <summary>Adds a character of no width to the cell of a screen row (after the marks it has already).</summary>
    internal void AddMark(int row, int column, string mark)
    {
        AddMarkToLine(_host.ToDocumentPosition(new GridPoint(0, row)).Line, column, mark);
    }

    /// <summary>Adds a character of no width to the cell of a line of the document (the command line writes its rows itself).</summary>
    internal void AddMarkToLine(int documentLine, int column, string mark)
    {
        if (documentLine < 0 || column < 0)
        {
            return;
        }
        while (documentLine >= _lines.Count)
        {
            _lines.Add(null);
        }
        LineInfo lineInfo = _lines[documentLine] ?? (_lines[documentLine] = new LineInfo());
        if (lineInfo.Marks == null || lineInfo.Marks.Length <= column)
        {
            string[] marks = new string[Math.Max(column + 1, _host.VisibleColumns)];
            lineInfo.Marks?.CopyTo(marks, 0);
            lineInfo.Marks = marks;
        }
        lineInfo.Marks[column] += mark;
    }

    /// <summary>Takes the characters of no width off cells of a line of the document (a wide character there was blanked).</summary>
    internal void ClearMarks(int documentLine, int column, int count)
    {
        if (documentLine >= 0 && documentLine < _lines.Count)
        {
            ClearMarks(_lines[documentLine], column, count);
        }
    }

    private static void ClearMarks(LineInfo lineInfo, int column, int count)
    {
        string[] marks = lineInfo?.Marks;
        if (marks == null)
        {
            return;
        }
        // Counted from the column, so that a count up to int.MaxValue (to the end of the line) does not overflow.
        for (int index = Math.Max(0, column); index < marks.Length && index - column < count; index++)
        {
            marks[index] = null;
        }
    }

    public void ClearLine(int line)
    {
        TextDocument.TextPosition textPosition = _host.ToDocumentPosition(new GridPoint(0, line));
        if (textPosition.Line < _lines.Count)
        {
            _lines[textPosition.Line] = null;
        }
    }

    public void RemoveLines(int line, int count)
    {
        if (line < this._lines.Count)
        {
            count = Math.Min(count, this._lines.Count - line);
            this._lines.RemoveRange(line, count);
        }
    }

    public void InsertLines(int line, int count)
    {
        if (line < this._lines.Count)
        {
            this._lines.InsertRange(line, new LineInfo[count]);
        }
    }

    public void DeleteCells(GridPoint point, int count)
    {
        TextDocument.TextPosition textPosition = _host.ToDocumentPosition(point);
        if (textPosition.Line >= this._lines.Count || this._lines[textPosition.Line] == null)
        {
            return;
        }
        LineInfo lineInfo = this._lines[textPosition.Line];
        lineInfo.IsDimmed = false;
        lineInfo.IsWrapped = false;
        string[] marks = lineInfo.Marks;
        if (marks != null)
        {
            for (int column = textPosition.Column; column < marks.Length; column++)
            {
                marks[column] = column + count < marks.Length ? marks[column + count] : null;
            }
        }
        if (lineInfo.Cells == null)
        {
            // A line that was only marked (wrapped or dimmed) has no cell styles yet: there is nothing to shift.
            return;
        }
        for (int column = textPosition.Column; column < lineInfo.Cells.Length; column++)
        {
            int targetColumn = column + count;
            if (targetColumn < lineInfo.Cells.Length)
            {
                lineInfo.Cells[column] = lineInfo.Cells[targetColumn];
            }
            else
            {
                lineInfo.Cells[column] = CellStyle.Default;
            }
        }
    }

    public void InsertCells(GridPoint point, int count)
    {
        TextDocument.TextPosition textPosition = _host.ToDocumentPosition(point);
        if (textPosition.Line < this._lines.Count && this._lines[textPosition.Line] != null)
        {
            LineInfo lineInfo = this._lines[textPosition.Line];
            lineInfo.IsDimmed = false;
            lineInfo.IsWrapped = false;
            string[] marks = lineInfo.Marks;
            if (marks != null && count > 0)
            {
                // The marks go right with their cells; the cells put in have none.
                for (int column = marks.Length - 1; column >= textPosition.Column; column--)
                {
                    marks[column] = column - count >= textPosition.Column ? marks[column - count] : null;
                }
            }
            if (lineInfo.Cells == null || textPosition.Column >= lineInfo.Cells.Length || count <= 0)
            {
                return;
            }
            // Shift right-to-left so that every cell is moved before it is overwritten.
            for (int column = lineInfo.Cells.Length - 1; column >= textPosition.Column + count; column--)
            {
                lineInfo.Cells[column] = lineInfo.Cells[column - count];
            }
            CellStyle insertedStyle = lineInfo.Cells[textPosition.Column];
            for (int offset = 0; offset < count && textPosition.Column + offset < lineInfo.Cells.Length; offset++)
            {
                lineInfo.Cells[textPosition.Column + offset] = insertedStyle;
            }
        }
    }

    internal void Clear()
    {
        _lines.Clear();
    }

    internal void DimLines(int lineCount)
    {
        for (int line = 0; line < lineCount; line++)
        {
            if (line == _lines.Count)
            {
                _lines.Add(new LineInfo());
            }
            else if (_lines[line] == null)
            {
                _lines[line] = new LineInfo();
            }
            _lines[line].IsDimmed = true;
        }
    }

    public void MarkWrapped(int line)
    {
        TextDocument.TextPosition textPosition = _host.ToDocumentPosition(new GridPoint(0, line));
        while (textPosition.Line >= _lines.Count)
        {
            _lines.Add(null);
        }
        if (_lines[textPosition.Line] == null)
        {
            _lines[textPosition.Line] = new LineInfo();
        }
        _lines[textPosition.Line].IsWrapped = true;
    }
}
