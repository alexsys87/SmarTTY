using SmarTTY.Native;
using SmarTTY.TextEditing;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

[ToolboxBitmap(typeof(RichTextBox))]
public partial class TextEditorControl : EditableTextBrowser
{
    public delegate void CharTypingHandler(TextEditorControl textEditorControl, char ch, ref string text, ref TextDocument.TextPosition textPosition);

    public delegate void CharTypedHandler(TextEditorControl textEditorControl, char ch, TextDocument.TextPosition textPosition);

    public bool CopyLineWhenNoSelection = true;

    protected virtual bool IsEditable => true;

    public bool CanEdit => IsEditable;

    public event CharTypingHandler CharTyping;

    public event CharTypedHandler CharTyped;

    public TextEditorControl()
    {
        InitializeComponent();
    }

    protected virtual string GetSelectedText(TextDocument.TextPosition start, TextDocument.TextPosition end)
    {
        return base.EditableDocument.GetRangeText(start, end);
    }

    public void CopyAll()
    {
        try
        {
            string text = GetSelectedText(default, new TextDocument.TextPosition(0, base.EditableDocument.LineCount));
            if (CodeEditorPP.IsWindows())
            {
                Win32Clipboard.SetText(Handle, text);
            }
            else
            {
                Clipboard.SetText(text);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void CopySelection(bool select = true)
    {
        try
        {
            string text;
            if (!_hasSelection)
            {
                if (!select)
                {
                    return;
                }
                text = base.EditableDocument.GetLineText(CaretPosition.Line) + "\r\n";
            }
            else
            {
                text = GetSelectedText(SelectionStart, SelectionEnd);
            }
            if (CodeEditorPP.IsWindows())
            {
                Win32Clipboard.SetText(Handle, text);
            }
            else
            {
                Clipboard.SetText(text);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public virtual void Paste()
    {
        try
        {
            ReplaceText(text: (!CodeEditorPP.IsWindows()) ? Clipboard.GetText() : Win32Clipboard.GetText(Handle), start: CaretPosition, end: SelectionStart, select: !_hasSelection);
            UpdateMaxLineWidth(-1);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void Cut()
    {
        CopySelection();
        if (!_hasSelection)
        {
            int line = CaretPosition.Line;
            ReplaceText(new TextDocument.TextPosition(0, line), new TextDocument.TextPosition(0, line + 1), select: false, "");
        }
        else
        {
            ReplaceText(CaretPosition, SelectionStart, !_hasSelection, "");
        }
    }

    public string GetPaddingToColumn(int column, int columnIndex, int columnNumber)
    {
        int lineLength = IndexToColumn(column, columnIndex);
        if (lineLength >= columnNumber)
        {
            return null;
        }
        int value = columnNumber % TabSize;
        int count = (columnNumber - value - lineLength + TabSize - 1) / TabSize;
        return new string('\t', count) + new string(' ', value);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (e.KeyChar == '\t' && ((_hasSelection && SelectionStart != CaretPosition) || (Control.ModifierKeys & Keys.Shift) == Keys.Shift))
        {
            int value = 1;
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                value = -1;
            }
            Indent(value);
        }
        else
        {
            switch (e.KeyChar)
            {
                case '\r':
                    {
                        string content = base.EditableDocument.GetLineTerminator(CaretPosition.Line, "\n");
                        ReplaceText(CaretPosition, SelectionStart, !_hasSelection, content + GetIndentation(CaretPosition.Line));
                        break;
                    }
                case '\b':
                    if (_hasSelection)
                    {
                        ReplaceText(SelectionEnd, SelectionStart, select: false, "");
                    }
                    else
                    {
                        ReplaceText(CaretPosition, GetMovedPosition(CaretMove.Left), select: false, "");
                    }
                    UpdateMaxLineWidth(CaretPosition.Line);
                    break;
                default:
                    if (char.IsControl(e.KeyChar) && e.KeyChar != '\t')
                    {
                        break;
                    }
                    if (!_hasSelection && OverwriteMode && CaretPosition.Column < base.EditableDocument.GetLineText(CaretPosition.Line).Length)
                    {
                        ReplaceText(CaretPosition, new TextDocument.TextPosition(CaretPosition.Column + 1, CaretPosition.Line), select: false, new string(e.KeyChar, 1));
                    }
                    else
                    {
                        string text = new string(e.KeyChar, 1);
                        TextDocument.TextPosition textPosition = new TextDocument.TextPosition(-1, -1);
                        OnCharTyping(e.KeyChar, ref text, ref textPosition);
                        if (text != null)
                        {
                            if (!_hasSelection)
                            {
                                ReplaceText(CaretPosition, SelectionStart, select: true, text);
                            }
                            else
                            {
                                ReplaceText(SelectionEnd, SelectionStart, select: false, text);
                            }
                        }
                        if (textPosition.IsValid)
                        {
                            SetCaret(textPosition);
                        }
                        OnCharTyped(e.KeyChar, CaretPosition);
                    }
                    UpdateMaxLineWidth(CaretPosition.Line);
                    break;
            }
        }
        base.OnKeyPress(e);
    }

    protected virtual void OnCharTyping(char ch, ref string text, ref TextDocument.TextPosition textPosition)
    {
        if (CharTyping != null)
        {
            CharTyping(this, ch, ref text, ref textPosition);
        }
    }

    protected virtual void OnCharTyped(char ch, TextDocument.TextPosition textPosition)
    {
        if (CharTyped != null)
        {
            CharTyped(this, ch, textPosition);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEditable)
        {
            if (e.KeyCode == Keys.Delete)
            {
                if (_hasSelection)
                {
                    ReplaceText(SelectionEnd, SelectionStart, select: false, "");
                }
                else
                {
                    ReplaceText(CaretPosition, GetMovedPosition(CaretMove.Right), select: false, "");
                }
                UpdateMaxLineWidth(CaretPosition.Line);
            }
            if (e.Control && (e.KeyCode == Keys.Insert || e.KeyCode == Keys.C))
            {
                CopySelection(CopyLineWhenNoSelection);
                e.Handled = true;
            }
            else if ((e.Shift && e.KeyCode == Keys.Insert) || (e.Control && e.KeyCode == Keys.V))
            {
                Paste();
                e.Handled = true;
            }
            else if ((e.Shift && e.KeyCode == Keys.Delete) || (e.Control && e.KeyCode == Keys.X))
            {
                Cut();
                e.Handled = true;
            }
            else if (!e.Shift && e.Control && e.KeyCode == Keys.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if ((e.Shift && e.Control && e.KeyCode == Keys.Z) || (e.Control && e.KeyCode == Keys.Y))
            {
                Redo();
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }

    public void InsertText(string text)
    {
        ReplaceText(CaretPosition, SelectionStart, !((ReadonlyTextBrowser)this).HasSelection, text);
    }

    public string GetWordAtCaret(out int column, out int value)
    {
        column = -1;
        value = 0;
        string text = base.EditableDocument.GetLineText(CaretPosition.Line);
        if (CaretPosition.Column == 0)
        {
            return null;
        }
        column = CaretPosition.Column;
        while (column > 0 && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(text[column - 1]) != -1)
        {
            column--;
        }
        int itemIndex;
        for (itemIndex = CaretPosition.Column; itemIndex < text.Length && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(text[itemIndex]) != -1; itemIndex++)
        {
        }
        value = itemIndex - column;
        return text.Substring(column, value);
    }

    public void ReplaceWordAtCaret(string text)
    {
        if (GetWordAtCaret(out var item, out var entry) == null || entry == 0)
        {
            InsertText(text);
        }
        else
        {
            ReplaceText(new TextDocument.TextPosition(item, CaretPosition.Line), new TextDocument.TextPosition(item + entry, CaretPosition.Line), select: false, text);
        }
    }
}
