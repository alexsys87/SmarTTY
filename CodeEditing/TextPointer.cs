using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>What a press of the mouse selects as it is dragged: characters, words (a double click) or lines (a triple click, or a click in the margin).</summary>
public enum TextPointerUnit
{
    Character,
    Word,
    Line
}

/// <summary>
/// The selection that the left button of the mouse makes in a text view (stage 3D, for the WPF text view; the WinForms view keeps its own, with
/// its click timing): a click puts the caret and drops the selection, a click with Shift extends the selection from where it starts (or from the
/// caret), a double click selects a word and a triple click a line, and a drag goes on by the same unit from what the press selected. It works
/// on the <see cref="TextSelection"/> and returns where the caret goes; the view sets the caret, scrolls and draws.
/// </summary>
public sealed class TextPointer
{
    private readonly TextSelection _selection;

    private readonly CaretNavigator _navigator;

    private readonly Func<TextDocument> _document;

    // What the press selected (a point for a click): the drag keeps it selected.
    private TextDocument.TextPosition _pressStart;

    private TextDocument.TextPosition _pressEnd;

    public TextPointer(TextSelection selection, CaretNavigator navigator, Func<TextDocument> document)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>The button is down: a move of the mouse extends the selection.</summary>
    public bool Dragging { get; private set; }

    /// <summary>What the drag selects by.</summary>
    public TextPointerUnit Unit { get; private set; }

    /// <summary>
    /// A press of the left button at <paramref name="position"/>, the <paramref name="clicks"/>-th of a quick series (1, 2, 3; more count as 3), with
    /// Shift or not, while the caret is at <paramref name="caret"/>; <paramref name="lineUnit"/>: the press is in the margin and selects lines.
    /// Returns where the caret goes.
    /// </summary>
    public TextDocument.TextPosition Press(TextDocument.TextPosition position, int clicks, bool shift, TextDocument.TextPosition caret, bool lineUnit = false)
    {
        Dragging = true;
        if (shift)
        {
            // The selection goes on from where it starts; without one, from the caret.
            _selection.StartIfNone(caret);
            Unit = TextPointerUnit.Character;
            _pressStart = _selection.Anchor;
            _pressEnd = _selection.Anchor;
            return Extend(position);
        }
        Unit = lineUnit || clicks >= 3 ? TextPointerUnit.Line : (clicks == 2 ? TextPointerUnit.Word : TextPointerUnit.Character);
        Bounds(position, out _pressStart, out _pressEnd);
        if (_pressStart == _pressEnd)
        {
            _selection.Clear();
            _selection.Anchor = _pressStart;
            return _pressStart;
        }
        _selection.HasSelection = true;
        _selection.Anchor = _pressStart;
        return _pressEnd;
    }

    /// <summary>The mouse moved to <paramref name="position"/> with the button down; returns where the caret goes (null while no press is dragged).</summary>
    public TextDocument.TextPosition? DragTo(TextDocument.TextPosition position)
    {
        if (!Dragging)
        {
            return null;
        }
        return Extend(position);
    }

    /// <summary>The button was let go.</summary>
    public void Release()
    {
        Dragging = false;
    }

    private TextDocument.TextPosition Extend(TextDocument.TextPosition position)
    {
        Bounds(position, out TextDocument.TextPosition start, out TextDocument.TextPosition end);
        TextDocument.TextPosition anchor;
        TextDocument.TextPosition caret;
        if (start < _pressStart)
        {
            // Before what the press selected: from its end back to the start of the unit under the mouse.
            anchor = _pressEnd;
            caret = start;
        }
        else
        {
            anchor = _pressStart;
            caret = end;
        }
        _selection.Anchor = anchor;
        _selection.HasSelection = anchor != caret;
        return caret;
    }

    // The unit of the drag around a position: the point itself, the word (as a double click finds it: a blank or a mark is a word of one
    // character, an empty line has none and gives the point), or the line with its line break.
    private void Bounds(TextDocument.TextPosition position, out TextDocument.TextPosition start, out TextDocument.TextPosition end)
    {
        switch (Unit)
        {
            case TextPointerUnit.Word:
                if (_navigator.TryGetWordBounds(position, out start, out end))
                {
                    return;
                }
                break;
            case TextPointerUnit.Line:
                {
                    TextDocument document = _document();
                    start = new TextDocument.TextPosition(0, position.Line);
                    end = (position.Line + 1 < document.LineCount)
                        ? new TextDocument.TextPosition(0, position.Line + 1)
                        : new TextDocument.TextPosition(document.GetLineText(position.Line).Length, position.Line);
                    return;
                }
        }
        start = position;
        end = position;
    }
}
