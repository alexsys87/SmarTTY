using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SmarTTY.Controls;

/// <summary>
/// The data inspector beside the bytes of <see cref="HexEditorControl"/>: the bytes at the caret read as integers of
/// every size, floating point numbers, a 32-bit time and a UTF-8 character, in the byte order of the editor. A click on
/// the byte order switches it; a double-click (or Enter) on a value lets the user type a new one, which replaces the
/// bytes at the caret; Ctrl+C copies the chosen value.
/// </summary>
internal sealed class HexDataInspector : Control
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    private const int InnerMargin = 6;

    private const int LabelChars = 12;

    private const int ValueChars = 25;

    // The rows above the values: the title and the byte order.
    private const int ValueRowsStart = 2;

    private readonly HexEditorControl _owner;

    private readonly TextBox _editBox = new TextBox();

    // The value being edited, and the value row chosen with the mouse or the arrow keys (-1: none).
    private HexValueType? _editing;

    private int _selectedRow = -1;

    private int _hoverRow = -1;

    // The answer to the last edit: an error, or empty.
    private string _message = "";

    private bool _messageIsError;

    // The offset the message is about: it goes away when the caret moves on.
    private int _messageOffset = -1;

    public HexDataInspector(HexEditorControl owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, value: true);
        TabStop = true;
        _editBox.Visible = false;
        _editBox.BorderStyle = BorderStyle.FixedSingle;
        _editBox.KeyDown += OnEditBoxKeyDown;
        _editBox.LostFocus += (sender, e) => EndEdit();
        Controls.Add(_editBox);
    }

    /// <summary>The width that shows a label and the longest value.</summary>
    public int PreferredWidth => 2 * InnerMargin + (LabelChars + ValueChars) * CharWidth + 1;

    private int CharWidth => Math.Max(1, TextRenderer.MeasureText("0", Font, new Size(1000, 1000), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);

    private int RowHeight => Math.Max(1, TextRenderer.MeasureText("0", Font, new Size(1000, 1000), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height) + 3;

    private int ValueX => InnerMargin + LabelChars * CharWidth;

    private static IReadOnlyList<HexValueType> Types => HexValueFormats.InspectorTypes;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        Color back = HexEditorControl.Blend(_owner.BackColor, _owner.ForeColor, 0.05f);
        Color fore = _owner.ForeColor;
        Color dim = HexEditorControl.Blend(_owner.BackColor, _owner.ForeColor, 0.55f);
        bool dark = _owner.BackColor.GetBrightness() < 0.5f;
        Color link = dark ? Color.FromArgb(120, 180, 255) : Color.FromArgb(0, 90, 200);
        Color error = dark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(190, 30, 30);
        graphics.Clear(back);
        using (Pen separator = new Pen(HexEditorControl.Blend(_owner.BackColor, _owner.ForeColor, 0.3f)))
        {
            graphics.DrawLine(separator, 0, 0, 0, Height);
        }
        int rowHeight = RowHeight;
        int valueX = ValueX;
        int width = Width - valueX - InnerMargin;
        int y = 2;
        TextRenderer.DrawText(graphics, "DATA INSPECTOR", Font, new Point(InnerMargin, y), dim, TextFlags);
        y += rowHeight + 4;

        DrawText(graphics, "Byte order", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
        DrawText(graphics, _owner.BigEndian ? "Big endian" : "Little endian", new Rectangle(valueX, y, width, rowHeight), link);
        y += rowHeight;

        byte[] bytes = _owner.GetBytesAtCaret(8);
        bool readOnly = _owner.ReadOnly;
        for (int index = 0; index < Types.Count; index++)
        {
            HexValueType type = Types[index];
            Rectangle rowBounds = new Rectangle(1, y, Width - 1, rowHeight);
            if (index == _selectedRow && Focused)
            {
                using SolidBrush selection = new SolidBrush(SystemColors.Highlight);
                graphics.FillRectangle(selection, rowBounds);
            }
            else if (index == _hoverRow || index == _selectedRow)
            {
                using SolidBrush hover = new SolidBrush(HexEditorControl.Blend(back, fore, 0.1f));
                graphics.FillRectangle(hover, rowBounds);
            }
            bool selected = index == _selectedRow && Focused;
            string label = HexValueFormats.GetName(type) + (HexValueFormats.UsesByteOrder(type) ? (_owner.BigEndian ? " BE" : " LE") : "");
            DrawText(graphics, label, new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), selected ? SystemColors.HighlightText : dim);
            bool known = HexValueFormats.TryRead(bytes, type, _owner.BigEndian, out string value);
            DrawText(graphics, known ? value : "-", new Rectangle(valueX, y, width, rowHeight), selected ? SystemColors.HighlightText : (known ? fore : dim));
            y += rowHeight;
        }

        y += 4;
        int offset = _owner.HasSelection ? _owner.SelectionStart : _owner.CaretOffset;
        if (offset != _messageOffset && _editing == null)
        {
            _message = "";
            _messageIsError = false;
        }
        DrawText(graphics, "Offset", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
        DrawText(graphics, "0x" + offset.ToString("X8", CultureInfo.InvariantCulture) + " (" + offset.ToString(CultureInfo.InvariantCulture) + ")",
            new Rectangle(valueX, y, width, rowHeight), fore);
        y += rowHeight;
        if (_owner.HasSelection)
        {
            DrawText(graphics, "Selection", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
            DrawText(graphics, HexEditorControl.FormatByteCount(_owner.SelectionLength), new Rectangle(valueX, y, width, rowHeight), fore);
            y += rowHeight;
        }
        HexHighlight highlight = _owner.Document.GetHighlightAt(offset);
        if (highlight != null)
        {
            Rectangle swatch = new Rectangle(InnerMargin, y + 3, rowHeight - 6, rowHeight - 6);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(highlight.Argb)))
            {
                graphics.FillRectangle(brush, swatch);
            }
            DrawText(graphics, "Marked", new Rectangle(InnerMargin + rowHeight, y, valueX - InnerMargin - rowHeight, rowHeight), dim);
            string text = highlight.Label.Length > 0 ? highlight.Label : HexEditorControl.FormatByteCount(highlight.Length);
            DrawText(graphics, text, new Rectangle(valueX, y, width, rowHeight), fore);
            y += rowHeight;
        }

        y += 4;
        string hint = _message.Length > 0
            ? _message
            : (readOnly ? "Click the byte order to switch it." : "Double-click a value to change it.");
        Rectangle hintBounds = new Rectangle(InnerMargin, y, Width - 2 * InnerMargin, Math.Max(rowHeight, Height - y));
        TextRenderer.DrawText(graphics, hint, Font, hintBounds, _messageIsError ? error : dim,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
    }

    private void DrawText(Graphics graphics, string text, Rectangle bounds, Color color)
    {
        int y = bounds.Y + Math.Max(0, (bounds.Height - Font.Height) / 2);
        TextRenderer.DrawText(graphics, text, Font, new Rectangle(bounds.X, y, Math.Max(1, bounds.Width), Font.Height), color, TextFlags);
    }

    /// <summary>The value row under <paramref name="y"/>, or -1; -2 for the byte order.</summary>
    private int RowAt(int y)
    {
        int rowHeight = RowHeight;
        int first = 2 + rowHeight + 4;
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

    private Rectangle ValueBounds(int row)
    {
        int rowHeight = RowHeight;
        int y = 2 + rowHeight + 4 + (row + ValueRowsStart - 1) * rowHeight;
        return new Rectangle(ValueX - 3, y, Math.Max(20, Width - ValueX - InnerMargin + 3), rowHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Y);
        Cursor = (row == -2 && e.X >= ValueX) ? Cursors.Hand : Cursors.Default;
        int hover = Math.Max(-1, row);
        if (hover != _hoverRow)
        {
            _hoverRow = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1)
        {
            _hoverRow = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        int row = RowAt(e.Y);
        if (row == -2)
        {
            _owner.BigEndian = !_owner.BigEndian;
            _owner.Focus();
            return;
        }
        Focus();
        if (row >= 0)
        {
            _selectedRow = row;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int row = RowAt(e.Y);
        if (e.Button == MouseButtons.Left && row >= 0)
        {
            BeginEdit(row);
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Up:
                _selectedRow = Math.Max(0, _selectedRow - 1);
                break;
            case Keys.Down:
                _selectedRow = Math.Min(Types.Count - 1, _selectedRow + 1);
                break;
            case Keys.Enter:
            case Keys.F2:
                if (_selectedRow >= 0)
                {
                    BeginEdit(_selectedRow);
                }
                break;
            case Keys.Escape:
                _owner.Focus();
                break;
            case Keys.C when e.Control:
                CopySelectedValue();
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_selectedRow < 0)
        {
            _selectedRow = 0;
        }
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void CopySelectedValue()
    {
        if (_selectedRow < 0 || !HexValueFormats.TryRead(_owner.GetBytesAtCaret(8), Types[_selectedRow], _owner.BigEndian, out string value))
        {
            return;
        }
        try
        {
            Clipboard.SetText(value);
            ShowMessage(HexValueFormats.GetName(Types[_selectedRow]) + " copied.", isError: false);
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
    }

    private void BeginEdit(int row)
    {
        if (_owner.ReadOnly)
        {
            ShowMessage("The file cannot be changed now.", isError: true);
            return;
        }
        HexValueType type = Types[row];
        _selectedRow = row;
        _editing = type;
        HexValueFormats.TryRead(_owner.GetBytesAtCaret(8), type, _owner.BigEndian, out string value);
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
        Rectangle bounds = ValueBounds(row);
        _editBox.Font = Font;
        _editBox.BackColor = _owner.BackColor;
        _editBox.ForeColor = _owner.ForeColor;
        _editBox.Bounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(bounds.Height, _editBox.PreferredHeight));
        _editBox.Text = value;
        _editBox.Visible = true;
        _editBox.Focus();
        _editBox.SelectAll();
        ShowMessage("Enter writes the value at the caret (" + (_owner.BigEndian ? "big" : "little") + " endian), Esc cancels.", isError: false);
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShowMessage("", isError: false);
            EndEdit();
            Focus();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_editing == null)
            {
                return;
            }
            if (_owner.WriteValue(_editing.Value, _editBox.Text, out string error))
            {
                ShowMessage(HexValueFormats.GetName(_editing.Value) + " written: Ctrl+Z in the bytes undoes it.", isError: false);
                EndEdit();
                Focus();
            }
            else
            {
                ShowMessage(error, isError: true);
                _editBox.SelectAll();
            }
        }
    }

    private void EndEdit()
    {
        _editing = null;
        if (_editBox.Visible)
        {
            _editBox.Visible = false;
        }
        Invalidate();
    }

    private void ShowMessage(string message, bool isError)
    {
        _message = message ?? "";
        _messageIsError = isError;
        _messageOffset = _owner.HasSelection ? _owner.SelectionStart : _owner.CaretOffset;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _editBox.Font = Font;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _editBox.Dispose();
        }
        base.Dispose(disposing);
    }
}
