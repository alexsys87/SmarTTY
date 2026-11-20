using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>A move of the caret by a key.</summary>
public enum CaretMove
{
    Left,
    Right,
    Up,
    Down,
    WordLeft,
    WordRight,
    Home,
    End,
    PageUp,
    PageDown
}

/// <summary>
/// What the moves of the caret read of the text view and ask it (made of what <c>ComputeMovedPosition</c> and <c>TryGetWordBounds</c> of the view
/// used): the text, the tab size, the rows in view, word wrap and its map, what a word is, the line shown before and after a line (a fold hides
/// lines, a provider adds rows between them) and what happens at the ends of the text.
/// </summary>
public interface ICaretNavigationHost
{
    /// <summary>The text shown (read each time: the view may be given another document).</summary>
    TextDocument Document { get; }

    int TabSize { get; }

    /// <summary>The rows in view: a page.</summary>
    int VisibleRows { get; }

    /// <summary>The lines are wrapped (the moves up and down go by the rows of <see cref="Wrap"/>).</summary>
    bool IsWrapping { get; }

    WrapMap Wrap { get; }

    bool IsWordChar(char ch);

    /// <summary>The line shown before <paramref name="line"/>.</summary>
    int GetPreviousRealLine(int line);

    /// <summary>The line shown after <paramref name="line"/>.</summary>
    int GetNextRealLine(int line);

    /// <summary>A move goes beyond the start (a negative direction) or the end of the text: the view may move the position.</summary>
    void OnMoveBeyondDocument(ref TextDocument.TextPosition textPosition, int direction);
}

/// <summary>
/// Where the caret of the text view goes by a key: by a character, a word, a line (or a row of a wrapped line), a page, to the start of the text
/// of a line or of the line, to its end, to the ends of the text; the column that Up, Down and the pages keep while they run; and the word under
/// the caret. It is what <c>Controls/ReadonlyTextBrowser</c> did in <c>ComputeMovedPosition</c>, <c>TryGetNavigationTarget</c> (the kept column),
/// <c>MoveWrappedRow</c>, <c>MoveWrappedRows</c> and <c>TryGetWordBounds</c>, moved here as it was (stage 3C); the view maps the keys, sets the
/// caret, scrolls and draws.
/// </summary>
public sealed class CaretNavigator
{
    private readonly ICaretNavigationHost _host;

    public CaretNavigator(ICaretNavigationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>The display column Up, Down and the pages keep while they run; -1 when it is taken anew at the next of them.</summary>
    public int DesiredColumn { get; set; } = -1;

    /// <summary>Where the kept column sits inside its row while the lines are wrapped.</summary>
    public int DesiredWrapOffset { get; set; }

    /// <summary>
    /// A navigation key is about to move the caret: Up, Down and the pages (<paramref name="keepsColumn"/>) take the column of the caret when none is
    /// kept, the other keys drop it.
    /// </summary>
    public void BeginMove(bool keepsColumn, TextDocument.TextPosition caret)
    {
        if (keepsColumn)
        {
            if (DesiredColumn == -1)
            {
                TextDocument document = _host.Document;
                DesiredColumn = document.IndexToColumn(caret.Line, caret.Column, _host.TabSize);
                DesiredWrapOffset = _host.IsWrapping ? DesiredColumn - _host.Wrap.SegmentStart(caret.Line, _host.Wrap.SegmentOf(caret.Line, DesiredColumn)) : 0;
            }
        }
        else
        {
            DesiredColumn = -1;
        }
    }

    /// <summary>
    /// The word the caret stands in or at the edge of (the one to its right first, as a click on the left half of a letter puts the
    /// caret in front of it), or the single character at it when that is not part of a word. False on an empty line.
    /// </summary>
    public bool TryGetWordBounds(TextDocument.TextPosition caret, out TextDocument.TextPosition start, out TextDocument.TextPosition end)
    {
        start = caret;
        end = caret;
        if (caret.Line < 0 || caret.Column < 0)
        {
            return false;
        }
        string text = _host.Document.GetLineText(caret.Line);
        if (text.Length == 0)
        {
            return false;
        }
        int column = Math.Min(caret.Column, text.Length);
        int anchor;
        if (column < text.Length && _host.IsWordChar(text[column]))
        {
            anchor = column;
        }
        else if (column > 0 && _host.IsWordChar(text[column - 1]))
        {
            anchor = column - 1;
        }
        else
        {
            int single = column < text.Length ? column : column - 1;
            start.Column = single;
            end.Column = single + 1;
            return true;
        }
        int from = anchor;
        while (from > 0 && _host.IsWordChar(text[from - 1]))
        {
            from--;
        }
        int to = anchor + 1;
        while (to < text.Length && _host.IsWordChar(text[to]))
        {
            to++;
        }
        start.Column = from;
        end.Column = to;
        return true;
    }

    /// <summary>Where <paramref name="caretMove"/> takes a position; <paramref name="control"/>: Ctrl is held (Ctrl+Home and Ctrl+End go to the ends of the text).</summary>
    public TextDocument.TextPosition Move(CaretMove caretMove, bool control, TextDocument.TextPosition textPosition)
    {
        if (textPosition.Column < 0 || textPosition.Line < 0)
        {
            return default;
        }
        TextDocument document = _host.Document;
        int tabSize = _host.TabSize;
        int visibleRows = _host.VisibleRows;
        string text = document.GetLineText(textPosition.Line);
        switch (caretMove)
        {
            case CaretMove.Left:
            case CaretMove.WordLeft:
                if (textPosition.Column > 0)
                {
                    textPosition.Column--;
                    if (caretMove == CaretMove.WordLeft)
                    {
                        while (textPosition.Column > 0 && _host.IsWordChar(text[textPosition.Column - 1]) && _host.IsWordChar(text[textPosition.Column]))
                        {
                            textPosition.Column--;
                        }
                    }
                }
                else
                {
                    if (textPosition.Line == 0)
                    {
                        _host.OnMoveBeyondDocument(ref textPosition, -1);
                    }
                    if (textPosition.Line > 0)
                    {
                        textPosition.Line = _host.GetPreviousRealLine(textPosition.Line);
                        textPosition.Column = document.GetLineText(textPosition.Line).Length;
                    }
                }
                return textPosition;
            case CaretMove.Right:
            case CaretMove.WordRight:
                if (textPosition.Column < text.Length)
                {
                    textPosition.Column++;
                    if (caretMove == CaretMove.WordRight)
                    {
                        while (textPosition.Column > 0 && textPosition.Column < text.Length && _host.IsWordChar(text[textPosition.Column - 1]) && _host.IsWordChar(text[textPosition.Column]))
                        {
                            textPosition.Column++;
                        }
                    }
                }
                else
                {
                    if (textPosition.Line == document.LineCount - 1)
                    {
                        _host.OnMoveBeyondDocument(ref textPosition, 1);
                    }
                    if (textPosition.Line < document.LineCount - 1)
                    {
                        textPosition.Line = _host.GetNextRealLine(textPosition.Line);
                        textPosition.Column = 0;
                    }
                }
                return textPosition;
            case CaretMove.Up:
                if (_host.IsWrapping)
                {
                    return MoveWrappedRow(textPosition, -1);
                }
                if (textPosition.Line == 0)
                {
                    _host.OnMoveBeyondDocument(ref textPosition, -1);
                }
                if (textPosition.Line > 0)
                {
                    textPosition.Line = _host.GetPreviousRealLine(textPosition.Line);
                    int index = document.ColumnToIndex(textPosition.Line, DesiredColumn, tabSize);
                    textPosition.Column = Math.Min(index, document.GetLineText(textPosition.Line).Length);
                }
                return textPosition;
            case CaretMove.Down:
                if (_host.IsWrapping)
                {
                    return MoveWrappedRow(textPosition, 1);
                }
                if (textPosition.Line == document.LineCount - 1)
                {
                    _host.OnMoveBeyondDocument(ref textPosition, 1);
                }
                if (textPosition.Line < document.LineCount - 1)
                {
                    textPosition.Line = _host.GetNextRealLine(textPosition.Line);
                    int columnIndexAfterDown = document.ColumnToIndex(textPosition.Line, DesiredColumn, tabSize);
                    textPosition.Column = Math.Min(columnIndexAfterDown, document.GetLineText(textPosition.Line).Length);
                }
                return textPosition;
            case CaretMove.Home:
                if (control)
                {
                    _host.OnMoveBeyondDocument(ref textPosition, int.MinValue);
                    textPosition.Column = 0;
                    textPosition.Line = 0;
                }
                else
                {
                    string lineText = document.GetLineText(textPosition.Line);
                    int indentLength;
                    for (indentLength = 0; indentLength < lineText.Length && (lineText[indentLength] == ' ' || lineText[indentLength] == '\t'); indentLength++)
                    {
                    }
                    if (textPosition.Column == indentLength)
                    {
                        textPosition.Column = 0;
                    }
                    else
                    {
                        textPosition.Column = indentLength;
                    }
                }
                return textPosition;
            case CaretMove.End:
                if (control)
                {
                    _host.OnMoveBeyondDocument(ref textPosition, int.MaxValue);
                    textPosition.Line = document.LineCount - 1;
                }
                textPosition.Column = document.GetLineText(textPosition.Line).Length;
                return textPosition;
            case CaretMove.PageUp:
                {
                    if (_host.IsWrapping)
                    {
                        return MoveWrappedRows(textPosition, -visibleRows);
                    }
                    textPosition.Line -= visibleRows;
                    if (textPosition.Line < 0)
                    {
                        _host.OnMoveBeyondDocument(ref textPosition, -visibleRows);
                    }
                    if (textPosition.Line < 0)
                    {
                        textPosition.Line = 0;
                    }
                    int columnAfterPageUp = document.ColumnToIndex(textPosition.Line, DesiredColumn, tabSize);
                    textPosition.Column = Math.Min(columnAfterPageUp, document.GetLineText(textPosition.Line).Length);
                    return textPosition;
                }
            case CaretMove.PageDown:
                {
                    if (_host.IsWrapping)
                    {
                        return MoveWrappedRows(textPosition, visibleRows);
                    }
                    textPosition.Line += visibleRows;
                    if (textPosition.Line >= document.LineCount)
                    {
                        _host.OnMoveBeyondDocument(ref textPosition, visibleRows);
                    }
                    if (textPosition.Line >= document.LineCount)
                    {
                        textPosition.Line = document.LineCount - 1;
                    }
                    int columnAfterPageDown = document.ColumnToIndex(textPosition.Line, DesiredColumn, tabSize);
                    textPosition.Column = Math.Min(columnAfterPageDown, document.GetLineText(textPosition.Line).Length);
                    return textPosition;
                }
            default:
                return textPosition;
        }
    }

    /// <summary>Moves a page of display rows up or down; at the top or the bottom the caret goes to the first or the last row.</summary>
    private TextDocument.TextPosition MoveWrappedRows(TextDocument.TextPosition textPosition, int rows)
    {
        WrapMap wrap = _host.Wrap;
        int column = _host.Document.IndexToColumn(textPosition.Line, textPosition.Column, _host.TabSize);
        int rowIndex = wrap.PositionToRowIndex(textPosition.Line, column);
        int target = Math.Max(0, Math.Min(rowIndex + rows, wrap.RowCount - 1));
        if (target == rowIndex)
        {
            return textPosition;
        }
        return MoveWrappedRow(textPosition, target - rowIndex);
    }

    /// <summary>Moves up or down by display rows, keeping the caret column inside the row.</summary>
    private TextDocument.TextPosition MoveWrappedRow(TextDocument.TextPosition textPosition, int direction)
    {
        TextDocument document = _host.Document;
        WrapMap wrap = _host.Wrap;
        int column = document.IndexToColumn(textPosition.Line, textPosition.Column, _host.TabSize);
        int rowIndex = wrap.PositionToRowIndex(textPosition.Line, column);
        int target = rowIndex + direction;
        if (target < 0 || target >= wrap.RowCount)
        {
            return textPosition;
        }
        int offset = DesiredColumn == -1 ? column - wrap.SegmentStart(textPosition.Line, wrap.SegmentOf(textPosition.Line, column)) : DesiredWrapOffset;
        int line = wrap.RowIndexToLine(target, out int segment);
        int targetColumn = wrap.SegmentStart(line, segment) + offset;
        int segmentEnd = wrap.SegmentEnd(line, segment);
        if (segmentEnd != int.MaxValue)
        {
            targetColumn = Math.Min(targetColumn, segmentEnd - 1);
        }
        int index = document.ColumnToIndex(line, targetColumn, _host.TabSize);
        textPosition.Line = line;
        textPosition.Column = Math.Min(index, document.GetLineText(line).Length);
        return textPosition;
    }
}
