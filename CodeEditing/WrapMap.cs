using System;

namespace SmarTTY.CodeEditing;

/// <summary>What the wrap map reads of the text view each time it is built (made of what <c>EnsureWrapMap</c> of the view read).</summary>
public interface IWrapMapSource
{
    int LineCount { get; }

    string GetLineText(int line);

    /// <summary>The width in columns the lines are wrapped to.</summary>
    int WrapWidth { get; }

    int TabSize { get; }
}

/// <summary>
/// Where the lines of the text view break while word wrap is on: for every line the display columns where its rows after the first one start, and
/// the index of its first row, built again when the text, the width or the tab size changed; the line and the row of a row index, the row of a
/// display column and the columns a row shows. It is what <c>Controls/ReadonlyTextBrowser.Wrap.cs</c> kept in its fields and computed in
/// <c>EnsureWrapMap</c>, <c>RowIndexToLine</c>, <c>WrapSegmentOf</c>, <c>WrapSegmentStart</c>, <c>WrapSegmentEnd</c> and <c>PositionToRowIndex</c>,
/// moved here as it was (stage 3C) so that a text view of another toolkit can use it.
/// </summary>
public sealed class WrapMap
{
    private readonly IWrapMapSource _source;

    private int _builtWidth = -1;

    private int _builtTabSize = -1;

    private bool _dirty = true;

    // Per line: the display columns where the rows after the first one start (null: the line takes one row).
    private int[][] _breaks = new int[0][];

    // Per line: the index of its first row; one more entry holds the number of all rows.
    private int[] _firstRow = new int[] { 0 };

    public WrapMap(IWrapMapSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Per line, the display columns where its rows after the first one start (null: one row), as last built.</summary>
    public int[][] Breaks => _breaks;

    /// <summary>Per line, the index of its first row, and one more entry with the number of all rows, as last built.</summary>
    public int[] FirstRows => _firstRow;

    /// <summary>The text changed: the map is built again before it is used.</summary>
    public void Invalidate()
    {
        _dirty = true;
    }

    /// <summary>Builds the map again when the text, the width or the tab size changed since it was built.</summary>
    public void Ensure()
    {
        int lineCount = _source.LineCount;
        if (!_dirty && _builtWidth == _source.WrapWidth && _builtTabSize == _source.TabSize && _breaks.Length == lineCount)
        {
            return;
        }
        int[][] breaks = new int[lineCount][];
        int[] firstRow = new int[lineCount + 1];
        int rows = 0;
        for (int line = 0; line < lineCount; line++)
        {
            firstRow[line] = rows;
            int[] lineBreaks = LineWrapper.ComputeBreaks(_source.GetLineText(line), _source.WrapWidth, _source.TabSize);
            breaks[line] = lineBreaks;
            rows += 1 + (lineBreaks == null ? 0 : lineBreaks.Length);
        }
        firstRow[lineCount] = rows;
        _breaks = breaks;
        _firstRow = firstRow;
        _builtWidth = _source.WrapWidth;
        _builtTabSize = _source.TabSize;
        _dirty = false;
    }

    /// <summary>The number of all rows.</summary>
    public int RowCount
    {
        get
        {
            Ensure();
            return _firstRow[_firstRow.Length - 1];
        }
    }

    /// <summary>The line and the row of that line (0 for the first) shown at a row index.</summary>
    public int RowIndexToLine(int rowIndex, out int segment)
    {
        Ensure();
        int lineCount = _source.LineCount;
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
            if (_firstRow[middle] <= rowIndex)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        segment = Math.Min(rowIndex - _firstRow[low], _breaks[low] == null ? 0 : _breaks[low].Length);
        return low;
    }

    /// <summary>The row of a line (0 for the first) that shows a display column.</summary>
    public int SegmentOf(int line, int displayColumn)
    {
        Ensure();
        if (line < 0 || line >= _breaks.Length || _breaks[line] == null)
        {
            return 0;
        }
        int[] lineBreaks = _breaks[line];
        int segment = 0;
        while (segment < lineBreaks.Length && lineBreaks[segment] <= displayColumn)
        {
            segment++;
        }
        return segment;
    }

    /// <summary>The first display column of a row of a line (the map as last built).</summary>
    public int SegmentStart(int line, int segment)
    {
        return (segment == 0 || _breaks[line] == null) ? 0 : _breaks[line][segment - 1];
    }

    /// <summary>The display column after the last one of a row of a line, or <see cref="int.MaxValue"/> for its last row (the map as last built).</summary>
    public int SegmentEnd(int line, int segment)
    {
        int[] lineBreaks = _breaks[line];
        return (lineBreaks == null || segment >= lineBreaks.Length) ? int.MaxValue : lineBreaks[segment];
    }

    /// <summary>The row index of a display column of a line (the line brought into the text).</summary>
    public int PositionToRowIndex(int line, int displayColumn)
    {
        Ensure();
        line = Math.Max(0, Math.Min(line, _breaks.Length - 1));
        return _firstRow[line] + SegmentOf(line, displayColumn);
    }
}
