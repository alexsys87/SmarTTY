using System;

namespace SmarTTY.TextEditing;

/// <summary>What a key does in the hex editor.</summary>
public enum HexKeyAction
{
    /// <summary>The key is not one of the editor's: it goes on (a character is typed through the text input).</summary>
    None,
    SelectAll,
    Copy,
    Cut,
    Paste,
    Undo,
    Redo,
    Find,
    GoToOffset,
    ToggleDataInspector,
    Save,
    DocumentStart,
    DocumentEnd,
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
    RowStart,
    RowEnd,
    Backspace,
    Delete,
    ToggleOverwrite,
    FindNext,
    FindPrevious,
    NextHighlight,
    PreviousHighlight
}

/// <summary>
/// The keys of the hex editor: which action a key with its modifiers is (<see cref="Map"/>), and what the actions that only move the caret or change
/// the bytes do to the model (<see cref="Apply"/>). It is the switch of <c>HexEditorControl.OnKeyDown</c>, moved here as it was; the view runs the
/// other actions (the clipboard, the questions, the search, the data inspector) and marks the key handled when the action is not <c>None</c>.
/// </summary>
public static class HexKeyMap
{
    /// <summary>
    /// The action of <paramref name="key"/>. Ctrl and Shift count only without Alt; <paramref name="extendSelection"/> is Shift for the movements.
    /// </summary>
    public static HexKeyAction Map(VirtualKey key, bool control, bool shift, bool alt, out bool extendSelection)
    {
        bool withControl = control && !alt;
        extendSelection = shift && !alt;
        if (withControl)
        {
            switch (key)
            {
                case VirtualKey.A:
                    return HexKeyAction.SelectAll;
                case VirtualKey.C:
                    return HexKeyAction.Copy;
                case VirtualKey.X:
                    return HexKeyAction.Cut;
                case VirtualKey.V:
                    return HexKeyAction.Paste;
                case VirtualKey.Z:
                    return shift ? HexKeyAction.Redo : HexKeyAction.Undo;
                case VirtualKey.Y:
                    return HexKeyAction.Redo;
                case VirtualKey.F:
                    return HexKeyAction.Find;
                case VirtualKey.G:
                    return HexKeyAction.GoToOffset;
                case VirtualKey.I:
                    return HexKeyAction.ToggleDataInspector;
                case VirtualKey.S:
                    return HexKeyAction.Save;
                case VirtualKey.Home:
                    return HexKeyAction.DocumentStart;
                case VirtualKey.End:
                    return HexKeyAction.DocumentEnd;
                default:
                    return HexKeyAction.None;
            }
        }
        switch (key)
        {
            case VirtualKey.Left:
                return HexKeyAction.Left;
            case VirtualKey.Right:
                return HexKeyAction.Right;
            case VirtualKey.Up:
                return HexKeyAction.Up;
            case VirtualKey.Down:
                return HexKeyAction.Down;
            case VirtualKey.PageUp:
                return HexKeyAction.PageUp;
            case VirtualKey.PageDown:
                return HexKeyAction.PageDown;
            case VirtualKey.Home:
                return HexKeyAction.RowStart;
            case VirtualKey.End:
                return HexKeyAction.RowEnd;
            case VirtualKey.Back:
                return HexKeyAction.Backspace;
            case VirtualKey.Delete:
                return HexKeyAction.Delete;
            case VirtualKey.Insert:
                return HexKeyAction.ToggleOverwrite;
            case VirtualKey.F3:
                return extendSelection ? HexKeyAction.FindPrevious : HexKeyAction.FindNext;
            case VirtualKey.F2:
                return extendSelection ? HexKeyAction.PreviousHighlight : HexKeyAction.NextHighlight;
            default:
                return HexKeyAction.None;
        }
    }

    /// <summary>
    /// Does an action that only moves the caret (a page is <paramref name="visibleRows"/> less one) or deletes bytes; false for the other actions,
    /// which the view runs.
    /// </summary>
    public static bool Apply(HexEditingModel model, HexKeyAction action, bool extendSelection, int visibleRows)
    {
        switch (action)
        {
            case HexKeyAction.DocumentStart:
                model.MoveTo(0, extendSelection, 0);
                return true;
            case HexKeyAction.DocumentEnd:
                model.MoveTo(model.Document.Length, extendSelection, 0);
                return true;
            case HexKeyAction.Left:
                model.MoveHorizontally(left: true, extendSelection: extendSelection);
                return true;
            case HexKeyAction.Right:
                model.MoveHorizontally(left: false, extendSelection: extendSelection);
                return true;
            case HexKeyAction.Up:
                model.MoveVertically(-1, extendSelection);
                return true;
            case HexKeyAction.Down:
                model.MoveVertically(1, extendSelection);
                return true;
            case HexKeyAction.PageUp:
                model.MoveVertically(-Math.Max(1, visibleRows - 1), extendSelection);
                return true;
            case HexKeyAction.PageDown:
                model.MoveVertically(Math.Max(1, visibleRows - 1), extendSelection);
                return true;
            case HexKeyAction.RowStart:
                model.MoveTo((model.CaretOffset / model.BytesPerRow) * model.BytesPerRow, extendSelection, model.Nibble);
                return true;
            case HexKeyAction.RowEnd:
                model.MoveTo(Math.Min(model.Document.Length, (model.CaretOffset / model.BytesPerRow) * model.BytesPerRow + model.BytesPerRow - 1), extendSelection, model.Nibble);
                return true;
            case HexKeyAction.Backspace:
                model.Backspace();
                return true;
            case HexKeyAction.Delete:
                model.DeleteCurrentSelectionOrByte();
                return true;
            default:
                return false;
        }
    }
}
