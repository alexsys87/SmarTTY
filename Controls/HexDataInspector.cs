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
/// bytes at the caret; Ctrl+C copies the chosen value. What it decides is <see cref="HexInspectorModel"/>; this control
/// draws it, keeps the edit box and the focus, and uses the clipboard.
/// </summary>
internal sealed class HexDataInspector : Control
{
    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    private const int InnerMargin = HexInspectorModel.InnerMargin;

    private const int LabelChars = HexInspectorModel.LabelChars;

    private const int ValueChars = HexInspectorModel.ValueChars;

    private readonly HexEditorControl _owner;

    private readonly HexInspectorModel _model;

    private readonly TextBox _editBox = new TextBox();

    public HexDataInspector(HexEditorControl owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _model = new HexInspectorModel(owner);
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
        int y = HexInspectorModel.TitleTop;
        TextRenderer.DrawText(graphics, HexInspectorModel.Title, Font, new Point(InnerMargin, y), dim, TextFlags);
        Rectangle hide = HideLinkBounds;
        TextRenderer.DrawText(graphics, HideText, Font, new Point(hide.X, y), link, TextFlags);
        y += rowHeight + HexInspectorModel.BlockGap;

        DrawText(graphics, "Byte order", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
        DrawText(graphics, _model.ByteOrderText, new Rectangle(valueX, y, width, rowHeight), link);
        y += rowHeight;

        IReadOnlyList<HexInspectorRow> rows = _model.ReadRows();
        int selectedRow = _model.SelectedRow;
        for (int index = 0; index < rows.Count; index++)
        {
            HexInspectorRow row = rows[index];
            Rectangle rowBounds = new Rectangle(1, y, Width - 1, rowHeight);
            if (index == selectedRow && Focused)
            {
                using SolidBrush selection = new SolidBrush(SystemColors.Highlight);
                graphics.FillRectangle(selection, rowBounds);
            }
            else if (index == _model.HoverRow || index == selectedRow)
            {
                using SolidBrush hover = new SolidBrush(HexEditorControl.Blend(back, fore, 0.1f));
                graphics.FillRectangle(hover, rowBounds);
            }
            bool selected = index == selectedRow && Focused;
            DrawText(graphics, row.Label, new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), selected ? SystemColors.HighlightText : dim);
            DrawText(graphics, row.Value, new Rectangle(valueX, y, width, rowHeight), selected ? SystemColors.HighlightText : (row.Known ? fore : dim));
            y += rowHeight;
        }

        y += HexInspectorModel.BlockGap;
        _model.ForgetMessageIfMoved();
        DrawText(graphics, "Offset", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
        DrawText(graphics, _model.OffsetText, new Rectangle(valueX, y, width, rowHeight), fore);
        y += rowHeight;
        string selectionText = _model.SelectionText;
        if (selectionText != null)
        {
            DrawText(graphics, "Selection", new Rectangle(InnerMargin, y, valueX - InnerMargin, rowHeight), dim);
            DrawText(graphics, selectionText, new Rectangle(valueX, y, width, rowHeight), fore);
            y += rowHeight;
        }
        HexHighlight highlight = _model.Highlight;
        if (highlight != null)
        {
            Rectangle swatch = new Rectangle(InnerMargin, y + 3, rowHeight - 6, rowHeight - 6);
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(highlight.Argb)))
            {
                graphics.FillRectangle(brush, swatch);
            }
            DrawText(graphics, "Marked", new Rectangle(InnerMargin + rowHeight, y, valueX - InnerMargin - rowHeight, rowHeight), dim);
            DrawText(graphics, HexInspectorModel.HighlightText(highlight), new Rectangle(valueX, y, width, rowHeight), fore);
            y += rowHeight;
        }

        y += HexInspectorModel.BlockGap;
        Rectangle hintBounds = new Rectangle(InnerMargin, y, Width - 2 * InnerMargin, Math.Max(rowHeight, Height - y));
        TextRenderer.DrawText(graphics, _model.Hint, Font, hintBounds, _model.MessageIsError ? error : dim,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
    }

    private void DrawText(Graphics graphics, string text, Rectangle bounds, Color color)
    {
        int y = bounds.Y + Math.Max(0, (bounds.Height - Font.Height) / 2);
        TextRenderer.DrawText(graphics, text, Font, new Rectangle(bounds.X, y, Math.Max(1, bounds.Width), Font.Height), color, TextFlags);
    }

    private const string HideText = HexInspectorModel.HideText;

    // The link in the title row that hides the inspector.
    private Rectangle HideLinkBounds
    {
        get
        {
            Size size = TextRenderer.MeasureText(HideText, Font, new Size(int.MaxValue, int.MaxValue), TextFlags);
            return new Rectangle(Width - InnerMargin - size.Width, 0, size.Width, RowHeight + 4);
        }
    }

    /// <summary>The value row under <paramref name="y"/>, or -1; -2 for the byte order.</summary>
    private int RowAt(int y)
    {
        return HexInspectorModel.RowAt(y, RowHeight);
    }

    private Rectangle ValueBounds(int row)
    {
        int rowHeight = RowHeight;
        int y = HexInspectorModel.ValueRowTop(row, rowHeight);
        return new Rectangle(ValueX - 3, y, Math.Max(20, Width - ValueX - InnerMargin + 3), rowHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = RowAt(e.Y);
        Cursor = HexInspectorModel.IsLink(row, e.X >= ValueX, HideLinkBounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        if (_model.SetHover(row))
        {
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_model.SetHover(-1))
        {
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
        switch (_model.Press(row, HideLinkBounds.Contains(e.Location)))
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
        if (row >= 0)
        {
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
        switch (_model.Key(e.KeyCode.ToVirtualKey(), e.Control))
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
                CopySelectedValue();
                break;
            case HexInspectorCommand.NotHandled:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _model.OnFocused();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void CopySelectedValue()
    {
        if (!_model.TryGetSelectedValue(out string value))
        {
            return;
        }
        try
        {
            Clipboard.SetText(value);
            _model.Copied();
            Invalidate();
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
    }

    private void BeginEdit(int row)
    {
        if (!_model.BeginEdit(row, out string value))
        {
            Invalidate();
            return;
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
        Invalidate();
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _model.CancelEdit();
            EndEdit();
            Focus();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            switch (_model.Commit(_editBox.Text))
            {
                case HexInspectorCommit.Unchanged:
                case HexInspectorCommit.Written:
                    EndEdit();
                    Focus();
                    break;
                case HexInspectorCommit.Rejected:
                    Invalidate();
                    _editBox.SelectAll();
                    break;
            }
        }
    }

    private void EndEdit()
    {
        _model.EndEdit();
        if (_editBox.Visible)
        {
            _editBox.Visible = false;
        }
        Invalidate();
    }

    private void ShowMessage(string message, bool isError)
    {
        _model.ShowMessage(message, isError);
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
