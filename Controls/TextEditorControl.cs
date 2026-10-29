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
    private bool TryCopyToClipboard(string text)
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
        TextDocument.TextPosition start;
        TextDocument.TextPosition end;
        if (last < base.EditableDocument.LineCount - 1)
        {
            start = new TextDocument.TextPosition(0, first);
            end = new TextDocument.TextPosition(0, last + 1);
        }
        else
        {
            // The last lines have no line break of their own: the one before them goes.
            start = (first > 0)
                ? new TextDocument.TextPosition(base.EditableDocument.GetLineText(first - 1).Length, first - 1)
                : new TextDocument.TextPosition(0, 0);
            end = new TextDocument.TextPosition(base.EditableDocument.GetLineText(last).Length, last);
        }
        ReplaceText(start, end, select: false, "");
    }

    /// <summary>Removes the selected text; does nothing without a selection (unlike Cut, it leaves the clipboard alone).</summary>
    public virtual void DeleteSelection()
    {
        if (IsEditable && _hasSelection)
        {
            ReplaceText(SelectionEnd, SelectionStart, select: false, "");
            UpdateMaxLineWidth(CaretPosition.Line);
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        // Tab indents a multi-line selection; a selection inside one line is replaced by the tab as usual.
        if (e.KeyChar == '\t' && ((_hasSelection && SelectionStart.Line != CaretPosition.Line) || (Control.ModifierKeys & Keys.Shift) == Keys.Shift))
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
                        if (content.Length == 0)
                        {
                            // The line has an inherited ending (an empty file or a file with a single line
                            // and no trailing newline): Enter must still insert a line break.
                            content = TextDocument.GetLineEndingText(base.EditableDocument.DefaultLineEnding);
                        }
                        // Only the indentation in front of the caret is carried over: with the caret inside it, the
                        // rest of the indentation stays with the text that moves to the new line.
                        string indentation = GetIndentation(CaretPosition.Line);
                        if (CaretPosition.Column < indentation.Length)
                        {
                            indentation = indentation.Substring(0, CaretPosition.Column);
                        }
                        ReplaceText(CaretPosition, SelectionStart, !_hasSelection, content + indentation);
                        break;
                    }
                case '\b':
                    if (_hasSelection)
                    {
                        ReplaceText(SelectionEnd, SelectionStart, select: false, "");
                    }
                    else if (GetSpaceIndentBackspaceLength() > 0)
                    {
                        // In space indentation Backspace removes one level, back to the previous tab stop.
                        int spaces = GetSpaceIndentBackspaceLength();
                        ReplaceText(CaretPosition, new TextDocument.TextPosition(CaretPosition.Column - spaces, CaretPosition.Line), select: false, "");
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
                        if (e.KeyChar == '\t' && InsertSpaces)
                        {
                            text = GetSpacesToNextTabStop();
                        }
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

    /// <summary>The spaces that move the insertion point (the caret or the start of the selection) to the next tab stop.</summary>
    private string GetSpacesToNextTabStop()
    {
        TextDocument.TextPosition position = CaretPosition;
        if (_hasSelection && SelectionStart < position)
        {
            position = SelectionStart;
        }
        int tabSize = Math.Max(1, TabSize);
        int column = IndexToColumn(position.Line, position.Column);
        return new string(' ', tabSize - column % tabSize);
    }

    /// <summary>
    /// With InsertSpaces, the number of spaces Backspace removes when the caret is inside leading spaces:
    /// back to the previous tab stop. 0 when Backspace should remove a single character.
    /// </summary>
    private int GetSpaceIndentBackspaceLength()
    {
        if (!InsertSpaces || CaretPosition.Column == 0)
        {
            return 0;
        }
        string lineText = base.EditableDocument.GetLineText(CaretPosition.Line);
        if (CaretPosition.Column > lineText.Length)
        {
            return 0;
        }
        for (int i = 0; i < CaretPosition.Column; i++)
        {
            if (lineText[i] != ' ')
            {
                return 0;
            }
        }
        int tabSize = Math.Max(1, TabSize);
        return (CaretPosition.Column - 1) % tabSize + 1;
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
            // Shift+Delete is Cut (handled below); deleting here first would lose the selection and
            // make Cut remove the whole caret line as well.
            if (e.KeyCode == Keys.Delete && !e.Shift)
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
            // Ctrl+Alt is AltGr on many keyboards: AltGr+C, V, X, Z, Y type letters there (ć, ż, ...), not commands.
            bool command = e.Control && !e.Alt;
            if (command && (e.KeyCode == Keys.Insert || e.KeyCode == Keys.C))
            {
                CopySelection(CopyLineWhenNoSelection);
                e.Handled = true;
            }
            else if ((e.Shift && e.KeyCode == Keys.Insert) || (command && e.KeyCode == Keys.V))
            {
                Paste();
                e.Handled = true;
            }
            else if ((e.Shift && e.KeyCode == Keys.Delete) || (command && e.KeyCode == Keys.X))
            {
                Cut();
                e.Handled = true;
            }
            else if (!e.Shift && command && e.KeyCode == Keys.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if ((e.Shift && command && e.KeyCode == Keys.Z) || (command && e.KeyCode == Keys.Y))
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
