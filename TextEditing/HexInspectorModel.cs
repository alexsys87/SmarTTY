using System;
using System.Collections.Generic;
using System.Globalization;

namespace SmarTTY.TextEditing;

/// <summary>What the data inspector reads and writes of the hex view it is beside (made of the calls <c>HexDataInspector</c> made on its owner).</summary>
public interface IHexInspectorSource
{
    bool BigEndian { get; set; }

    /// <summary>The bytes cannot be changed.</summary>
    bool ReadOnly { get; }

    bool HasSelection { get; }

    int SelectionStart { get; }

    int SelectionLength { get; }

    int CaretOffset { get; }

    HexDocument Document { get; }

    /// <summary>Up to <paramref name="count"/> bytes from the caret.</summary>
    byte[] GetBytesAtCaret(int count);

    /// <summary>Writes a value typed by the user at the caret; false with the reason when the text is not a value of the type.</summary>
    bool WriteValue(HexValueType type, string text, out string error);
}

/// <summary>What the view of the inspector does after a key or a click the model handled.</summary>
public enum HexInspectorCommand
{
    /// <summary>The key is not the inspector's: the view lets it go on.</summary>
    NotHandled,

    /// <summary>Done: the view draws again.</summary>
    Handled,

    /// <summary>The view opens its edit box on <see cref="HexInspectorModel.SelectedRow"/> (<see cref="HexInspectorModel.BeginEdit"/>).</summary>
    BeginEdit,

    /// <summary>The view gives the focus back to the bytes.</summary>
    FocusEditor,

    /// <summary>The view hides the inspector and gives the focus back to the bytes.</summary>
    HideInspector,

    /// <summary>The view copies the value of the chosen row (<see cref="HexInspectorModel.TryGetSelectedValue"/>).</summary>
    CopyValue,

    /// <summary>The view takes the focus to the inspector.</summary>
    FocusInspector
}

/// <summary>The answer to Enter in the edit box of a value.</summary>
public enum HexInspectorCommit
{
    /// <summary>Nothing is being edited.</summary>
    NotEditing,

    /// <summary>The text is the one the box started with: nothing is written, the edit ends.</summary>
    Unchanged,

    /// <summary>The value is written at the caret, the edit ends.</summary>
    Written,

    /// <summary>The text is not a value of the type: the message says why, the edit goes on.</summary>
    Rejected
}

/// <summary>A row of values: its label, its value (or "-") and whether the bytes give one.</summary>
public readonly struct HexInspectorRow
{
    public HexInspectorRow(string label, string value, bool known)
    {
        Label = label;
        Value = value;
        Known = known;
    }

    public string Label { get; }

    public string Value { get; }

    public bool Known { get; }
}

/// <summary>
/// The data inspector beside the bytes of the hex view without its window: the bytes at the caret read as integers of every size, floating point
/// numbers, a 32-bit time and a UTF-8 character in the byte order of the view, the row chosen with the mouse or the keys, the value being edited and
/// the text its box starts with, what Enter, Esc, the keys and the clicks do, the message about the last edit and when it goes away, and where the
/// rows are. It is what <c>Controls/HexDataInspector</c> decided, moved here as it was (stage 3B-3) so that the WPF inspector does the same; a view
/// draws it, keeps the edit box and the focus, and uses the clipboard.
/// </summary>
public sealed class HexInspectorModel
{
    /// <summary>The space at the edges of the inspector.</summary>
    public const int InnerMargin = 6;

    /// <summary>The width of the labels, in characters.</summary>
    public const int LabelChars = 12;

    /// <summary>The width of the values, in characters.</summary>
    public const int ValueChars = 25;

    /// <summary>The title of the inspector.</summary>
    public const string Title = "DATA INSPECTOR";

    /// <summary>The link in the title row that hides the inspector.</summary>
    public const string HideText = "Hide (Ctrl+I)";

    /// <summary>The top of the title row.</summary>
    public const int TitleTop = 2;

    /// <summary>The space below the title row and the gaps between the blocks of rows.</summary>
    public const int BlockGap = 4;

    // The rows above the values: the title and the byte order.
    private const int ValueRowsStart = 2;

    private readonly IHexInspectorSource _source;

    // The offset the message is about: it goes away when the caret moves on.
    private int _messageOffset = -1;

    public HexInspectorModel(IHexInspectorSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>The values, in the order of the rows.</summary>
    public static IReadOnlyList<HexValueType> Types => HexValueFormats.InspectorTypes;

    /// <summary>The value row chosen with the mouse or the arrow keys (-1: none).</summary>
    public int SelectedRow { get; set; } = -1;

    /// <summary>The value row under the mouse (-1: none).</summary>
    public int HoverRow { get; private set; } = -1;

    /// <summary>The value being edited, or null.</summary>
    public HexValueType? Editing { get; private set; }

    /// <summary>The text the edit box was filled with.</summary>
    public string EditOriginalText { get; private set; }

    /// <summary>The answer to the last edit (or copy): an error, or empty.</summary>
    public string Message { get; private set; } = "";

    public bool MessageIsError { get; private set; }

    /// <summary>The text of the byte order row.</summary>
    public string ByteOrderText => _source.BigEndian ? "Big endian" : "Little endian";

    /// <summary>The value rows for the bytes at the caret now.</summary>
    public IReadOnlyList<HexInspectorRow> ReadRows()
    {
        byte[] bytes = _source.GetBytesAtCaret(8);
        HexInspectorRow[] rows = new HexInspectorRow[Types.Count];
        for (int index = 0; index < Types.Count; index++)
        {
            HexValueType type = Types[index];
            string label = HexValueFormats.GetName(type) + (HexValueFormats.UsesByteOrder(type) ? (_source.BigEndian ? " BE" : " LE") : "");
            bool known = HexValueFormats.TryRead(bytes, type, _source.BigEndian, out string value);
            rows[index] = new HexInspectorRow(label, known ? value : "-", known);
        }
        return rows;
    }

    /// <summary>The offset the inspector is about: the start of the selection, or the caret.</summary>
    public int Offset => _source.HasSelection ? _source.SelectionStart : _source.CaretOffset;

    /// <summary>The text of the offset row.</summary>
    public string OffsetText
    {
        get
        {
            int offset = Offset;
            return "0x" + offset.ToString("X8", CultureInfo.InvariantCulture) + " (" + offset.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>The text of the selection row, or null when there is no selection (no row).</summary>
    public string SelectionText => _source.HasSelection ? HexEditingModel.FormatByteCount(_source.SelectionLength) : null;

    /// <summary>The coloured block at <see cref="Offset"/>, or null (no row).</summary>
    public HexHighlight Highlight => _source.Document.GetHighlightAt(Offset);

    /// <summary>The text of the row of a coloured block: its label, or its size.</summary>
    public static string HighlightText(HexHighlight highlight)
    {
        return highlight.Label.Length > 0 ? highlight.Label : HexEditingModel.FormatByteCount(highlight.Length);
    }

    /// <summary>
    /// Drops the message when the caret has moved on from the offset it is about and nothing is being edited. The view calls it when it draws, after
    /// the value rows and before the offset row, where the old control did it.
    /// </summary>
    public void ForgetMessageIfMoved()
    {
        if (Offset != _messageOffset && Editing == null)
        {
            Message = "";
            MessageIsError = false;
        }
    }

    /// <summary>The text at the bottom: the message, or what the inspector can do.</summary>
    public string Hint => Message.Length > 0
        ? Message
        : (_source.ReadOnly ? "Click the byte order to switch it." : "Double-click a value to change it.");

    /// <summary>The top of the byte order row.</summary>
    public static int ByteOrderRowTop(int rowHeight)
    {
        return TitleTop + rowHeight + BlockGap;
    }

    /// <summary>The top of a value row.</summary>
    public static int ValueRowTop(int row, int rowHeight)
    {
        return TitleTop + rowHeight + BlockGap + (row + ValueRowsStart - 1) * rowHeight;
    }

    /// <summary>The value row under <paramref name="y"/>, or -1; -2 for the byte order.</summary>
    public static int RowAt(int y, int rowHeight)
    {
        int first = TitleTop + rowHeight + BlockGap;
        if (y < first)
        {
            return -1;
        }
        int row = (y - first) / rowHeight - (ValueRowsStart - 1);
        if (row == -1)
        {
            return -2;
        }
        return (row >= 0 && row < Types.Count) ? row : -1;
    }

    /// <summary>The mouse points at something that can be clicked: the byte order (right of the labels) or the Hide link.</summary>
    public static bool IsLink(int row, bool inValueColumn, bool onHideLink)
    {
        return (row == -2 && inValueColumn) || onHideLink;
    }

    /// <summary>The mouse is over <paramref name="row"/> (see <see cref="RowAt"/>); true when the row under it changed.</summary>
    public bool SetHover(int row)
    {
        int hover = Math.Max(-1, row);
        if (hover == HoverRow)
        {
            return false;
        }
        HoverRow = hover;
        return true;
    }

    /// <summary>The inspector got the focus: the first row is chosen when none was.</summary>
    public void OnFocused()
    {
        if (SelectedRow < 0)
        {
            SelectedRow = 0;
        }
    }

    /// <summary>A press of the left button on <paramref name="row"/> (see <see cref="RowAt"/>), or on the Hide link.</summary>
    public HexInspectorCommand Press(int row, bool onHideLink)
    {
        if (onHideLink)
        {
            return HexInspectorCommand.HideInspector;
        }
        if (row == -2)
        {
            _source.BigEndian = !_source.BigEndian;
            return HexInspectorCommand.FocusEditor;
        }
        if (row >= 0)
        {
            SelectedRow = row;
        }
        return HexInspectorCommand.FocusInspector;
    }

    /// <summary>A key pressed while the inspector (not its edit box) has the focus.</summary>
    public HexInspectorCommand Key(VirtualKey key, bool control)
    {
        switch (key)
        {
            case VirtualKey.Up:
                SelectedRow = Math.Max(0, SelectedRow - 1);
                return HexInspectorCommand.Handled;
            case VirtualKey.Down:
                SelectedRow = Math.Min(Types.Count - 1, SelectedRow + 1);
                return HexInspectorCommand.Handled;
            case VirtualKey.Enter:
            case VirtualKey.F2:
                return SelectedRow >= 0 ? HexInspectorCommand.BeginEdit : HexInspectorCommand.Handled;
            case VirtualKey.Escape:
                return HexInspectorCommand.FocusEditor;
            case VirtualKey.I when control:
                return HexInspectorCommand.HideInspector;
            case VirtualKey.C when control:
                return HexInspectorCommand.CopyValue;
            default:
                return HexInspectorCommand.NotHandled;
        }
    }

    /// <summary>The value of the chosen row as it is shown, for the clipboard; false when no row is chosen or the bytes give no value.</summary>
    public bool TryGetSelectedValue(out string value)
    {
        value = null;
        return SelectedRow >= 0 && HexValueFormats.TryRead(_source.GetBytesAtCaret(8), Types[SelectedRow], _source.BigEndian, out value);
    }

    /// <summary>The value of the chosen row was copied.</summary>
    public void Copied()
    {
        ShowMessage(HexValueFormats.GetName(Types[SelectedRow]) + " copied.", isError: false);
    }

    /// <summary>
    /// Starts the edit of a value: false (with the message) when the file cannot be changed; otherwise the text the edit box starts with (a
    /// character without its code, a time without "UTC").
    /// </summary>
    public bool BeginEdit(int row, out string text)
    {
        text = null;
        if (_source.ReadOnly)
        {
            ShowMessage("The file cannot be changed now.", isError: true);
            return false;
        }
        HexValueType type = Types[row];
        SelectedRow = row;
        Editing = type;
        HexValueFormats.TryRead(_source.GetBytesAtCaret(8), type, _source.BigEndian, out string value);
        if (type == HexValueType.Utf8Char)
        {
            // Only the character itself, without its code.
            int quote = value.LastIndexOf('\'');
            value = (value.StartsWith("'", StringComparison.Ordinal) && quote > 0) ? value.Substring(1, quote - 1) : "";
        }
        else if (type == HexValueType.UnixTime32 && value.EndsWith(" UTC", StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - 4);
        }
        // What the box starts with: Enter on a value that was not changed writes nothing (a float or a time shown rounded
        // would be written back rounded, and the bytes would change with nothing typed).
        EditOriginalText = value;
        text = value;
        ShowMessage("Enter writes the value at the caret (" + (_source.BigEndian ? "big" : "little") + " endian), Esc cancels.", isError: false);
        return true;
    }

    /// <summary>Enter in the edit box with <paramref name="text"/>.</summary>
    public HexInspectorCommit Commit(string text)
    {
        if (Editing == null)
        {
            return HexInspectorCommit.NotEditing;
        }
        if (string.Equals(text, EditOriginalText, StringComparison.Ordinal))
        {
            ShowMessage("Unchanged: nothing written.", isError: false);
            EndEdit();
            return HexInspectorCommit.Unchanged;
        }
        if (_source.WriteValue(Editing.Value, text, out string error))
        {
            ShowMessage(HexValueFormats.GetName(Editing.Value) + " written: Ctrl+Z in the bytes undoes it.", isError: false);
            EndEdit();
            return HexInspectorCommit.Written;
        }
        ShowMessage(error, isError: true);
        return HexInspectorCommit.Rejected;
    }

    /// <summary>Esc in the edit box: the edit ends and the message goes.</summary>
    public void CancelEdit()
    {
        ShowMessage("", isError: false);
        EndEdit();
    }

    /// <summary>The edit ends (also when the edit box loses the focus).</summary>
    public void EndEdit()
    {
        Editing = null;
    }

    /// <summary>Shows a message about the offset the inspector is about now.</summary>
    public void ShowMessage(string message, bool isError)
    {
        Message = message ?? "";
        MessageIsError = isError;
        _messageOffset = Offset;
    }
}
