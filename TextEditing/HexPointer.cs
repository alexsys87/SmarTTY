namespace SmarTTY.TextEditing;

/// <summary>The button of a press in the bytes of the hex editor.</summary>
public enum HexPointerButton
{
    Left,
    Right,
    Other
}

/// <summary>What a press did, and so what the view does after it.</summary>
public enum HexPressResult
{
    /// <summary>Nothing (a button that is not the left or the right one).</summary>
    None,

    /// <summary>A right press in the selection: the column of the press is taken, the selection stays (the view redraws, for its menu).</summary>
    KeptSelection,

    /// <summary>A right press elsewhere: the caret went there without a selection (the view shows the caret, scrolls to it, redraws and tells it).</summary>
    MovedCaret,

    /// <summary>A left press: a drag selection starts (the model told the change; the view starts its automatic scrolling).</summary>
    StartedDrag
}

/// <summary>
/// The selection made with the mouse in the hex editor: a press puts the caret (Shift extends the selection from its anchor, a right press inside the
/// selection keeps it for the menu), a drag moves the end of the selection, a release ends the drag. It is what <c>HexEditorControl</c> did in
/// <c>OnMouseDown</c>, <c>DragTo</c> and <c>OnMouseUp</c> on the model, moved here as it was; the view finds the offset under the mouse
/// (<see cref="HexViewLayout.OffsetFromPoint"/>), keeps the press on the header and the link of the data inspector, and does the follow-up.
/// </summary>
public sealed class HexPointer
{
    private readonly HexEditingModel _model;

    private int _dragAnchor;

    public HexPointer(HexEditingModel model)
    {
        _model = model;
    }

    /// <summary>A drag selection runs (the left button is down since a press in the bytes).</summary>
    public bool Dragging { get; private set; }

    /// <summary>A press at <paramref name="offset"/> in <paramref name="area"/>, on the digit <paramref name="nibble"/>.</summary>
    public HexPressResult Press(HexPointerButton button, int offset, HexEditArea area, int nibble, bool shift)
    {
        if (button == HexPointerButton.Other)
        {
            return HexPressResult.None;
        }
        if (button == HexPointerButton.Right)
        {
            _model.SecondNibblePending = false;
            // A right click inside the selection opens the menu for it (Copy, Cut, Delete) and keeps it.
            if (_model.HasSelection && offset >= _model.SelectionStart && offset < _model.SelectionEnd)
            {
                _model.Area = area;
                return HexPressResult.KeptSelection;
            }
            _model.Anchor = -1;
            _model.CaretOffset = offset;
            _model.Area = area;
            _model.Nibble = nibble;
            Dragging = false;
            return HexPressResult.MovedCaret;
        }
        Dragging = true;
        // Shift+click extends the selection from its anchor, or from the caret when nothing is selected.
        _dragAnchor = shift ? (_model.Anchor >= 0 ? _model.Anchor : _model.CaretOffset) : offset;
        _model.Anchor = shift ? _dragAnchor : -1;
        _model.CaretOffset = offset;
        _model.Area = area;
        _model.Nibble = nibble;
        if (!shift)
        {
            _model.Anchor = offset;
        }
        _model.AfterCaretChange();
        return HexPressResult.StartedDrag;
    }

    /// <summary>The drag reached <paramref name="offset"/> in <paramref name="area"/>: the selection runs from the anchor to it (none when they meet).</summary>
    public void DragTo(int offset, HexEditArea area)
    {
        _model.Anchor = _dragAnchor;
        _model.CaretOffset = offset;
        _model.Area = area;
        if (_model.Anchor == _model.CaretOffset)
        {
            _model.Anchor = -1;
        }
    }

    /// <summary>The button was let go (or the view found it let go elsewhere).</summary>
    public void Release()
    {
        Dragging = false;
    }
}
