using SmarTTY.CodeEditing;
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

    /// <summary>Puts the text on the clipboard; false (after the error was shown) when the clipboard did not take it.</summary>
    protected bool TryCopyToClipboard(string text)
    {
        try
        {
            if (CodeEditorPP.IsWindows())
            {
                Win32Clipboard.SetText(Handle, text);
            }
            else
            {
                Clipboard.SetText(text);
            }
            return true;
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return false;
        }
    }

    public virtual void CopyAll()
    {
        TryCopyToClipboard(GetSelectedText(default, new TextDocument.TextPosition(0, base.EditableDocument.LineCount)));
    }

    public virtual void CopySelection(bool select = true)
    {
        if (!_hasSelection && !select)
        {
            return;
        }
        TryCopyToClipboard(GetTextToCopy());
    }

    /// <summary>The selection, or the line of the caret (with its line break) when nothing is selected.</summary>
    private string GetTextToCopy()
    {
        return _hasSelection
            ? GetSelectedText(SelectionStart, SelectionEnd)
            : base.EditableDocument.GetLineText(CaretPosition.Line) + "\r\n";
    }

    /// <summary>The text of the clipboard (null when it holds none).</summary>
    protected string ReadClipboardText()
    {
        return (!CodeEditorPP.IsWindows()) ? Clipboard.GetText() : Win32Clipboard.GetText(Handle);
    }

    public virtual void Paste()
    {
        PasteText(ReadClipboardText());
    }

    /// <summary>Pastes <paramref name="text"/> as if it came from the clipboard (the paste menu pastes its history and snippets this way).</summary>
    public virtual void PasteText(string text)
    {
        try
        {
            ReplaceText(text: text, start: CaretPosition, end: SelectionStart, select: !_hasSelection);
            UpdateMaxLineWidth(-1);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public virtual void Cut()
    {
        // The text is removed only when the clipboard has it.
        if (!TryCopyToClipboard(GetTextToCopy()))
        {
            return;
        }
        if (!_hasSelection)
        {
            RemoveLines(CaretPosition.Line, CaretPosition.Line);
        }
        else
        {
            ReplaceText(CaretPosition, SelectionStart, !_hasSelection, "");
        }
    }

    /// <summary>Removes the lines together with their line breaks (the line break before them when they are the last ones).</summary>
    protected void RemoveLines(int first, int last)
    {
        Typing.RemoveLines(first, last);
    }

    /// <summary>Removes the selected text; does nothing without a selection (unlike Cut, it leaves the clipboard alone).</summary>
    public virtual void DeleteSelection()
    {
        Typing.DeleteSelection();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        Typing.TypeCharacter(e.KeyChar, (Control.ModifierKeys & Keys.Shift) == Keys.Shift);
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
            TypingKeyResult result = Typing.KeyDown(e.KeyCode.ToVirtualKey(), e.Control, e.Alt, e.Shift);
            if ((result & TypingKeyResult.Handled) != 0)
            {
                e.Handled = true;
            }
            if ((result & TypingKeyResult.SuppressKeyPress) != 0)
            {
                e.SuppressKeyPress = true;
            }
        }
        base.OnKeyDown(e);
    }

    public void InsertText(string text)
    {
        Typing.InsertText(text);
    }

    public string GetWordAtCaret(out int column, out int value)
    {
        return Typing.GetWordAtCaret(out column, out value);
    }

    public void ReplaceWordAtCaret(string text)
    {
        Typing.ReplaceWordAtCaret(text);
    }
}
