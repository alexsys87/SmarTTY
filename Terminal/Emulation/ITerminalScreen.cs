namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// The screen as the parser of the escape sequences sees it: the cursor, the style of the next text, the tab stops, the scroll region,
/// the active buffer, and the operations that change the cells. Every member is one that the parser called on the control before.
/// </summary>
public interface ITerminalScreen
{
    /// <summary>The cursor, a column and a row of the screen (a value outside the screen is brought back to it).</summary>
    SmarTTY.TextEditing.GridPoint Cursor { get; set; }

    /// <summary>The number of rows of the screen.</summary>
    int Rows { get; }

    /// <summary>The number of columns of the screen.</summary>
    int Columns { get; }

    /// <summary>The tab stops (the terminal replaces them on a full reset, so read this each time).</summary>
    TabStops Tabs { get; }

    /// <summary>The style that the text written next gets.</summary>
    CellStyle CurrentStyle { get; set; }

    /// <summary>0 for the normal screen, 1 for the alternate one.</summary>
    int ActiveBufferIndex { get; set; }

    /// <summary>The first row of the scroll region.</summary>
    int ScrollTop { get; }

    /// <summary>The text pushes the characters after it to the right (insert mode, CSI 4 h) instead of replacing them.</summary>
    bool InsertMode { set; }

    /// <summary>The caret is hidden (CSI ? 25 l).</summary>
    bool HideCaret { set; }

    void MoveCursorBy(int deltaX, int deltaY, bool scroll = false);

    void InsertChars(int count);

    void DeleteChars(int count);

    void EraseInDisplay(EraseMode eraseMode);

    void EraseInLine(EraseMode eraseMode);

    void InsertBlankLines(int line, int count);

    void ScrollRegionUp(int topLine, int count);

    void WriteAt(SmarTTY.TextEditing.GridPoint point, string text, CellStyle cellStyle);

    void RepeatPreviousChar(int count);

    void SetScrollRegion(int top, int bottom);

    void ClearScrollRegion();

    void ClearActiveScreen();

    void InvalidateView();
}
