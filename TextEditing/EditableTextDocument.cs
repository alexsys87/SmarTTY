using System.Collections.Generic;

namespace SmarTTY.TextEditing;

public class EditableTextDocument : MutableTextDocument
{
    public TextPosition EndPosition
    {
        get
        {
            if (LineCount != 0)
            {
                return new TextPosition(GetLineText(LineCount - 1).Length, LineCount - 1);
            }
            return new TextPosition(0, 0);
        }
    }

    public EditableTextDocument()
    {
    }

    public EditableTextDocument(IEnumerable<string> items, LineEnding lineEnding)
    {
        foreach (string item in items)
        {
            Lines.Add(new DocumentLine(item, lineEnding));
        }
    }

    public TextPosition InsertText(TextPosition textPosition, string text2, bool flag = false)
    {
        if (text2.IndexOfAny(TextDocument._lineBreakChars) == -1)
        {
            base.ReplaceInLine(textPosition.Line, textPosition.Column, 0, text2);
            return new TextPosition(textPosition.Column + text2.Length, textPosition.Line);
        }
        List<DocumentLine> list = TextDocument.SplitLines(text2);
        if (list.Count < 1)
        {
            return textPosition;
        }
        if (flag && textPosition.Line >= 0 && textPosition.Line < Lines.Count)
        {
            LineEnding lineEnding = Lines[textPosition.Line].Ending;
            foreach (DocumentLine item in list)
            {
                item.Ending = lineEnding;
            }
        }
        if (Lines.Count == 1 && Lines[0].GetText().Length == 0)
        {
            Lines.Clear();
        }
        SplitLine(textPosition.Line, textPosition.Column, list[0].Ending);
        base.ReplaceInLine(textPosition.Line, textPosition.Column, 0, list[0].GetText(), list[0].Ending);
        for (int num = 1; num < list.Count - 1; num++)
        {
            InsertLine(++textPosition.Line, list[num].GetText(), list[num].Ending);
        }
        string text = list[list.Count - 1].GetText();
        if (textPosition.Line + 1 == Lines.Count)
        {
            InsertLine(textPosition.Line + 1, text, list[list.Count - 1].Ending);
        }
        else
        {
            base.ReplaceInLine(textPosition.Line + 1, 0, 0, text, list[0].Ending);
        }
        return new TextPosition(list[list.Count - 1].GetText().Length, textPosition.Line + 1);
    }

    public void SetLineEnding(LineEnding lineEnding)
    {
        foreach (DocumentLine item in Lines)
        {
            item.Ending = lineEnding;
        }
    }
}
