using SmarTTY.TextEditing;
using System;

// What Controls/HexDataInspector did around HexInspectorModel, without a window: the sizes (from the fixed char cell of HexReplayView), the edit box
// (its text, whether it is shown, the focus), the keys of a row and of the edit box, the mouse press, and the focus moves to the editor.
internal sealed class HexReplayInspector
{
    private readonly HexReplayView _owner;

    private readonly HexInspectorModel _model;

    public HexReplayInspector(HexReplayView owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _model = new HexInspectorModel(owner);
    }

    public HexInspectorModel Model => _model;

    /// <summary>The text of the edit box.</summary>
    public string EditText { get; set; } = "";

    /// <summary>The edit box is shown.</summary>
    public bool EditVisible { get; private set; }

    public int PreferredWidth => 2 * HexInspectorModel.InnerMargin + (HexInspectorModel.LabelChars + HexInspectorModel.ValueChars) * CharWidth + 1;

    private static int CharWidth => HexReplayView.CharWidth;

    public int RowHeight => HexReplayView.CharHeight + 3;

    public int ValueX => HexInspectorModel.InnerMargin + HexInspectorModel.LabelChars * CharWidth;

    /// <summary>The link that hides the inspector, at the right of the title row (the width of its text in the char cell).</summary>
    public void HideLinkBounds(out int x, out int y, out int width, out int height)
    {
        width = HexInspectorModel.HideText.Length * CharWidth;
        x = PreferredWidth - HexInspectorModel.InnerMargin - width;
        y = 0;
        height = RowHeight + 4;
    }

    private bool InHideLink(int px, int py)
    {
        HideLinkBounds(out int x, out int y, out int width, out int height);
        return px >= x && px < x + width && py >= y && py < y + height;
    }

    public int RowAt(int y)
    {
        return HexInspectorModel.RowAt(y, RowHeight);
    }

    public void Focus()
    {
        _owner.SetFocus(HexReplayFocus.Inspector);
    }

    public void OnGotFocus()
    {
        _model.OnFocused();
    }

    public void OnEditBoxLostFocus()
    {
        EndEdit();
    }

    /// <summary>A press of the left button (another button does nothing).</summary>
    public void MouseDown(HexReplayButton button, int x, int y)
    {
        if (button != HexReplayButton.Left)
        {
            return;
        }
        int row = RowAt(y);
        switch (_model.Press(row, InHideLink(x, y)))
        {
            case HexInspectorCommand.HideInspector:
                _owner.ShowDataInspector = false;
                _owner.Focus();
                return;
            case HexInspectorCommand.FocusEditor:
                _owner.Focus();
                return;
        }
        Focus();
    }

    /// <summary>A key while a row (not the edit box) has the focus.</summary>
    public void KeyDown(VirtualKey key, bool control)
    {
        switch (_model.Key(key, control))
        {
            case HexInspectorCommand.BeginEdit:
                BeginEdit(_model.SelectedRow);
                break;
            case HexInspectorCommand.FocusEditor:
                _owner.Focus();
                break;
            case HexInspectorCommand.HideInspector:
                _owner.ShowDataInspector = false;
                _owner.Focus();
                break;
            case HexInspectorCommand.CopyValue:
                if (_model.TryGetSelectedValue(out string value))
                {
                    _owner.Clipboard = value;
                    _model.Copied();
                }
                break;
        }
    }

    public void BeginEdit(int row)
    {
        if (!_model.BeginEdit(row, out string value))
        {
            return;
        }
        EditText = value;
        EditVisible = true;
        _owner.SetFocus(HexReplayFocus.EditBox);
    }

    /// <summary>A key in the edit box: Esc cancels, Enter writes.</summary>
    public void EditBoxKey(VirtualKey key)
    {
        if (key == VirtualKey.Escape)
        {
            _model.CancelEdit();
            EndEdit();
            Focus();
        }
        else if (key == VirtualKey.Enter)
        {
            switch (_model.Commit(EditText))
            {
                case HexInspectorCommit.Unchanged:
                case HexInspectorCommit.Written:
                    EndEdit();
                    Focus();
                    break;
            }
        }
    }

    private void EndEdit()
    {
        _model.EndEdit();
        EditVisible = false;
    }

    public void ShowMessage(string message, bool isError)
    {
        _model.ShowMessage(message, isError);
    }
}
