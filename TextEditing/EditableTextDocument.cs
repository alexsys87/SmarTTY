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

    public TextPosition InsertText(TextPosition position, string text, bool inheritLineEnding = false)
    {
        if (text.IndexOfAny(TextDocument._lineBreakChars) == -1)
        {
            base.ReplaceInLine(position.Line, position.Column, 0, text);
            return new TextPosition(position.Column + text.Length, position.Line);
        }
        List<DocumentLine> insertedLines = TextDocument.SplitLines(text);
        if (insertedLines.Count < 1)
        {
            return position;
        }
        if (inheritLineEnding && position.Line >= 0 && position.Line < Lines.Count)
        {
            LineEnding lineEnding = Lines[position.Line].Ending;
            foreach (DocumentLine line in insertedLines)
            {
                line.Ending = lineEnding;
            }
        }
        if (Lines.Count == 1 && Lines[0].GetText().Length == 0)
        {
            Lines.Clear();
        }
        SplitLine(position.Line, position.Column, insertedLines[0].Ending);
        base.ReplaceInLine(position.Line, position.Column, 0, insertedLines[0].GetText(), insertedLines[0].Ending);
        for (int i = 1; i < insertedLines.Count - 1; i++)
        {
            InsertLine(++position.Line, insertedLines[i].GetText(), insertedLines[i].Ending);
        }
        string lastLineText = insertedLines[insertedLines.Count - 1].GetText();
        if (position.Line + 1 == Lines.Count)
        {
            InsertLine(position.Line + 1, lastLineText, insertedLines[insertedLines.Count - 1].Ending);
        }
        else
        {
            base.ReplaceInLine(position.Line + 1, 0, 0, lastLineText, insertedLines[0].Ending);
        }
        return new TextPosition(insertedLines[insertedLines.Count - 1].GetText().Length, position.Line + 1);
    }

    public void SetLineEnding(LineEnding lineEnding)
    {
        foreach (DocumentLine line in Lines)
        {
            line.Ending = lineEnding;
        }
    }
}
