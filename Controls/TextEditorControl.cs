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

    protected virtual string GetSelectedText(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2)
    {
        return base.EditableDocument.GetRangeText(textPosition, textPosition2);
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void CopySelection(bool flag = true)
    {
        try
        {
            string text;
            if (!_hasSelection)
            {
                if (!flag)
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public virtual void Paste()
    {
        try
        {
            ReplaceText(text: (!CodeEditorPP.IsWindows()) ? Clipboard.GetText() : Win32Clipboard.GetText(Handle), textPosition: CaretPosition, textPosition2: SelectionStart, flag: !_hasSelection);
            UpdateMaxLineWidth(-1);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void Cut()
    {
        CopySelection();
        if (!_hasSelection)
        {
            int num = CaretPosition.Line;
            ReplaceText(new TextDocument.TextPosition(0, num), new TextDocument.TextPosition(0, num + 1), flag: false, "");
        }
        else
        {
            ReplaceText(CaretPosition, SelectionStart, !_hasSelection, "");
        }
    }

    public string GetPaddingToColumn(int value2, int value3, int value4)
    {
        int num = IndexToColumn(value2, value3);
        if (num >= value4)
        {
            return null;
        }
        int num2 = value4 % TabSize;
        int count = (value4 - num2 - num + TabSize - 1) / TabSize;
        return new string('\t', count) + new string(' ', num2);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (e.KeyChar == '\t' && ((_hasSelection && SelectionStart != CaretPosition) || (Control.ModifierKeys & Keys.Shift) == Keys.Shift))
        {
            int num = 1;
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                num = -1;
            }
            Indent(num);
        }
        else
        {
            switch (e.KeyChar)
            {
                case '\r':
                    {
                        string text2 = base.EditableDocument.GetLineTerminator(CaretPosition.Line, "\n");
                        ReplaceText(CaretPosition, SelectionStart, !_hasSelection, text2 + GetIndentation(CaretPosition.Line));
                        break;
                    }
                case '\b':
                    if (_hasSelection)
                    {
                        ReplaceText(SelectionEnd, SelectionStart, flag: false, "");
                    }
                    else
                    {
                        ReplaceText(CaretPosition, GetMovedPosition(CaretMove.Left), flag: false, "");
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
                        ReplaceText(CaretPosition, new TextDocument.TextPosition(CaretPosition.Column + 1, CaretPosition.Line), flag: false, new string(e.KeyChar, 1));
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
                                ReplaceText(CaretPosition, SelectionStart, flag: true, text);
                            }
                            else
                            {
                                ReplaceText(SelectionEnd, SelectionStart, flag: false, text);
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
                    ReplaceText(SelectionEnd, SelectionStart, flag: false, "");
                }
                else
                {
                    ReplaceText(CaretPosition, GetMovedPosition(CaretMove.Right), flag: false, "");
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

    public string GetWordAtCaret(out int value2, out int value3)
    {
        value2 = -1;
        value3 = 0;
        string text = base.EditableDocument.GetLineText(CaretPosition.Line);
        if (CaretPosition.Column == 0)
        {
            return null;
        }
        value2 = CaretPosition.Column;
        while (value2 > 0 && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(text[value2 - 1]) != -1)
        {
            value2--;
        }
        int num;
        for (num = CaretPosition.Column; num < text.Length && "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".IndexOf(text[num]) != -1; num++)
        {
        }
        value3 = num - value2;
        return text.Substring(value2, value3);
    }

    public void ReplaceWordAtCaret(string text)
    {
        if (GetWordAtCaret(out var num, out var num2) == null || num2 == 0)
        {
            InsertText(text);
        }
        else
        {
            ReplaceText(new TextDocument.TextPosition(num, CaretPosition.Line), new TextDocument.TextPosition(num + num2, CaretPosition.Line), flag: false, text);
        }
    }
}
