using System;

namespace SmarTTY.TextEditing;

/// <summary>
/// Where the parts of the hex editor are, in whole pixels of its view: the size of a character and of a row, the header, the columns of the
/// offsets, the hex digits (in groups) and the characters, the rows and the scroll position, the byte under a point, the place of the caret, what
/// keeps the caret in view, which scroll bars are needed, and the steps of the automatic scrolling and of the wheel. It is what
/// <c>Controls/HexEditorControl</c> computed in <c>UpdateMetrics</c>, <c>HexCellX</c>, <c>ByteAtPoint</c>, <c>OffsetFromPoint</c>,
/// <c>EnsureVisible</c>, <c>CaretX</c>, <c>UpdateScrollBars</c>, <c>OnAutoScrollTick</c> and <c>OnMouseWheel</c>, moved here as it was. The view
/// measures the font, tells the size of its client area, of the scroll bars and of the data inspector (<see cref="IHexLayoutView"/>), and draws.
/// </summary>
public sealed class HexViewLayout
{
    /// <summary>The digits of an offset at the start of a row.</summary>
    public const int OffsetDigits = 8;

    /// <summary>The space at the left and right edges, in pixels.</summary>
    public const int Margin = 6;

    private readonly HexEditingModel _model;

    private readonly IHexLayoutView _view;

    // The small steps of a touchpad's wheel that have not made a whole notch yet.
    private int _wheelRemainder;

    /// <param name="model">The caret, the area, the row width and the document of the editor.</param>
    /// <param name="view">The sizes of the view: its client area, its scroll bars and its data inspector, read when they are needed.</param>
    public HexViewLayout(HexEditingModel model, IHexLayoutView view)
    {
        _model = model;
        _view = view;
    }

    /// <summary>The width of a character.</summary>
    public int CharWidth { get; private set; } = 8;

    /// <summary>The height of a row of bytes.</summary>
    public int RowHeight { get; private set; } = 18;

    /// <summary>The height of the header with the column titles.</summary>
    public int HeaderHeight { get; private set; } = 22;

    /// <summary>The left edge of the hex digits, before scrolling.</summary>
    public int HexStart { get; private set; } = 80;

    /// <summary>The left edge of the characters, before scrolling.</summary>
    public int AsciiStart { get; private set; } = 480;

    /// <summary>The width of a row with all its columns.</summary>
    public int ContentWidth { get; private set; } = 640;

    /// <summary>The bytes shown together without a space: 1, 2, 4 or 8.</summary>
    public int GroupSize { get; set; } = 1;

    /// <summary>The row at the top of the table.</summary>
    public int FirstRow { get; set; }

    /// <summary>How far the columns are scrolled to the left, in pixels.</summary>
    public int ScrollX { get; set; }

    /// <summary>The columns are laid out for a character of <paramref name="charWidth"/> and a text of <paramref name="textHeight"/> pixels.</summary>
    public void SetMetrics(int charWidth, int textHeight)
    {
        CharWidth = Math.Max(1, charWidth);
        RowHeight = Math.Max(1, textHeight) + 3;
        HeaderHeight = RowHeight + 4;
        HexStart = Margin + OffsetDigits * CharWidth + 2 * CharWidth;
        AsciiStart = HexCellX(_model.BytesPerRow) + 2 * CharWidth;
        ContentWidth = AsciiStart + _model.BytesPerRow * CharWidth + Margin + 1;
    }

    /// <summary>The rows that are wholly visible.</summary>
    public int VisibleRows
    {
        get
        {
            int height = _view.ClientHeight - (_view.HorizontalBarVisible ? _view.HorizontalBarHeight : 0) - HeaderHeight - Margin;
            return Math.Max(1, height / Math.Max(1, RowHeight));
        }
    }

    /// <summary>The width the bytes are shown in: left of the vertical scroll bar and of the data inspector.</summary>
    public int VisibleWidth => Math.Max(1, AvailableWidth - (_view.VerticalBarVisible ? _view.VerticalBarWidth : 0));

    /// <summary>The width left for the bytes beside the data inspector.</summary>
    public int AvailableWidth => _view.ClientWidth - _view.InspectorWidth;

    /// <summary>One more row than the bytes fill when the last row is full: the caret sits there after the last byte.</summary>
    public int RowCount => _model.Document.Length / _model.BytesPerRow + 1;

    /// <summary>The left edge of the hex digits of <paramref name="column"/>, before scrolling.</summary>
    public int HexCellX(int column)
    {
        return HexStart + column * 2 * CharWidth + (column / GroupSize) * CharWidth;
    }

    /// <summary>The data inspector of <paramref name="inspectorWidth"/> fits beside the bytes: in a narrow window the bytes come first.</summary>
    public bool InspectorFits(int inspectorWidth)
    {
        return _view.ClientWidth >= inspectorWidth + HexStart + 8 * 3 * CharWidth;
    }

    /// <summary>The byte under the point (<paramref name="x"/>, <paramref name="y"/>) in the hex or the character column, or -1.</summary>
    public int ByteAtPoint(int x, int y)
    {
        if (y < HeaderHeight || x >= VisibleWidth)
        {
            return -1;
        }
        int row = (y - HeaderHeight) / Math.Max(1, RowHeight) + FirstRow;
        x += ScrollX;
        int column = -1;
        if (x >= HexStart && x < HexCellX(_model.BytesPerRow))
        {
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                if (x >= HexCellX(index) && x < HexCellX(index) + 2 * CharWidth)
                {
                    column = index;
                    break;
                }
            }
        }
        else if (x >= AsciiStart && x < AsciiStart + _model.BytesPerRow * CharWidth)
        {
            column = (x - AsciiStart) / Math.Max(1, CharWidth);
        }
        if (column < 0)
        {
            return -1;
        }
        int offset = row * _model.BytesPerRow + column;
        return (offset < _model.Document.Length) ? offset : -1;
    }

    /// <summary>
    /// Where a click at (<paramref name="x"/>, <paramref name="y"/>) puts the caret: the offset, the column and the digit. The gap after a byte
    /// belongs to it, left of the bytes is the first one of the row, right of the characters the last one.
    /// </summary>
    public int OffsetFromPoint(int x, int y, out HexEditArea area, out int nibble)
    {
        int row = Math.Max(0, (y - HeaderHeight) / Math.Max(1, RowHeight) + FirstRow);
        x += ScrollX;
        area = HexEditArea.Hex;
        nibble = 0;
        int column;
        if (x < HexStart)
        {
            column = 0;
        }
        else if (x < AsciiStart)
        {
            // The gap after a byte belongs to it: a click there puts the caret on its low digit.
            column = _model.BytesPerRow - 1;
            for (int index = 0; index < _model.BytesPerRow; index++)
            {
                if (x < HexCellX(index + 1))
                {
                    column = index;
                    break;
                }
            }
            nibble = (x - HexCellX(column) >= CharWidth) ? 1 : 0;
        }
        else
        {
            area = HexEditArea.Ascii;
            column = Math.Max(0, Math.Min(_model.BytesPerRow - 1, (x - AsciiStart) / Math.Max(1, CharWidth)));
        }
        return Math.Max(0, Math.Min(row * _model.BytesPerRow + column, _model.Document.Length));
    }

    /// <summary>The rows of bytes: below the header, above the horizontal scroll bar, left of the vertical one (and of the data inspector).</summary>
    public void GetTable(out int left, out int top, out int right, out int bottom)
    {
        int tableBottom = _view.ClientHeight - (_view.HorizontalBarVisible ? _view.HorizontalBarHeight : 0);
        left = 0;
        top = HeaderHeight;
        right = VisibleWidth;
        bottom = HeaderHeight + Math.Max(1, tableBottom - HeaderHeight);
    }

    /// <summary>The point brought into the table; <paramref name="outside"/> tells whether it was outside.</summary>
    public void ClampToTable(ref int x, ref int y, out bool outside)
    {
        GetTable(out int left, out int top, out int right, out int bottom);
        int clampedX = Math.Max(left, Math.Min(x, right - 1));
        int clampedY = Math.Max(top, Math.Min(y, bottom - 1));
        outside = clampedX != x || clampedY != y;
        x = clampedX;
        y = clampedY;
    }

    /// <summary>
    /// How far the table scrolls while a selection is dragged to (<paramref name="x"/>, <paramref name="y"/>) beyond its edge: rows up or down, and
    /// pixels left or right (by groups of three characters). Both are 0 inside the table.
    /// </summary>
    public void AutoScrollStep(int x, int y, out int rows, out int pixels)
    {
        GetTable(out int left, out int top, out int right, out int bottom);
        rows = 0;
        pixels = 0;
        if (y < top)
        {
            rows = -HexScroll.AutoScrollSteps(top - y, RowHeight);
        }
        else if (y >= bottom)
        {
            rows = HexScroll.AutoScrollSteps(y - bottom + 1, RowHeight);
        }
        if (x < left)
        {
            pixels = -HexScroll.AutoScrollSteps(left - x, CharWidth) * CharWidth * 3;
        }
        else if (x >= right)
        {
            pixels = HexScroll.AutoScrollSteps(x - right + 1, CharWidth) * CharWidth * 3;
        }
    }

    /// <summary>The rows a turn of the wheel by <paramref name="delta"/> scrolls (three a notch; the small steps of a touchpad add up).</summary>
    public int WheelRows(int delta)
    {
        return HexScroll.WheelNotches(ref _wheelRemainder, delta) * 3;
    }

    /// <summary>The first row brought into the content: the last rows fill the table.</summary>
    public int ClampFirstRow(int row)
    {
        return Math.Max(0, Math.Min(row, Math.Max(0, RowCount - VisibleRows)));
    }

    /// <summary>The left edge of the caret, before scrolling: in the hex digits or the characters, after the last byte at its place.</summary>
    public int CaretX()
    {
        int column = _model.CaretOffset % _model.BytesPerRow;
        if (_model.CaretOffset >= _model.Document.Length)
        {
            column = _model.Document.Length % _model.BytesPerRow;
        }
        return (_model.Area == HexEditArea.Ascii) ? AsciiStart + column * CharWidth : HexCellX(column);
    }

    /// <summary>Scrolls so that the caret is in view (the view then sets its scroll bars, which bring both values into the content).</summary>
    public void EnsureVisible()
    {
        int row = Math.Min(RowCount - 1, _model.CaretOffset / _model.BytesPerRow);
        int visibleRows = VisibleRows;
        if (row < FirstRow)
        {
            FirstRow = row;
        }
        else if (row >= FirstRow + visibleRows)
        {
            FirstRow = row - visibleRows + 1;
        }
        FirstRow = Math.Max(0, Math.Min(FirstRow, Math.Max(0, RowCount - visibleRows)));
        int x = CaretX();
        int width = VisibleWidth;
        if (x < ScrollX)
        {
            ScrollX = Math.Max(0, x - Margin);
        }
        else if (x + 2 * CharWidth > ScrollX + width)
        {
            ScrollX = Math.Max(0, x + 2 * CharWidth - width + Margin);
        }
    }

    /// <summary>Which scroll bars the content needs in the client area (each one takes room from the other).</summary>
    public void ChooseScrollBars(out bool needVertical, out bool needHorizontal)
    {
        int rowCount = RowCount;
        int visibleWithoutHorizontal = Math.Max(1, (_view.ClientHeight - HeaderHeight - Margin) / Math.Max(1, RowHeight));
        needVertical = rowCount > visibleWithoutHorizontal;
        needHorizontal = ContentWidth > AvailableWidth - (needVertical ? _view.VerticalBarWidth : 0);
        if (needHorizontal)
        {
            int visibleWithHorizontal = Math.Max(1, (_view.ClientHeight - HeaderHeight - Margin - _view.HorizontalBarHeight) / Math.Max(1, RowHeight));
            needVertical = rowCount > visibleWithHorizontal;
            needHorizontal = ContentWidth > AvailableWidth - (needVertical ? _view.VerticalBarWidth : 0);
        }
    }
}

/// <summary>The sizes of the view of the hex editor that its layout needs, read each time (made of what <c>HexEditorControl</c> read).</summary>
public interface IHexLayoutView
{
    /// <summary>The width of the client area.</summary>
    int ClientWidth { get; }

    /// <summary>The height of the client area.</summary>
    int ClientHeight { get; }

    /// <summary>The width the data inspector takes at the right, 0 when it is not shown.</summary>
    int InspectorWidth { get; }

    /// <summary>The width of the vertical scroll bar.</summary>
    int VerticalBarWidth { get; }

    /// <summary>The height of the horizontal scroll bar.</summary>
    int HorizontalBarHeight { get; }

    /// <summary>The vertical scroll bar is shown.</summary>
    bool VerticalBarVisible { get; }

    /// <summary>The horizontal scroll bar is shown.</summary>
    bool HorizontalBarVisible { get; }
}
