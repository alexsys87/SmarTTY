using System.Collections.Generic;

namespace SmarTTY.TextEditing;

public class MutableTextDocument : TextDocument
{
    private List<DocumentLine> LineList => Lines;

    public void OnClear()
    {
    }

    protected void InsertLine(int value2, string text, LineEnding lineEnding)
    {
        this.LineList.Insert(value2, new DocumentLine(text, lineEnding));
    }

    public void RemoveLines(int value2, int value3)
    {
        if (value2 >= 0 && value2 < this.LineList.Count)
        {
            if (value3 > this.LineList.Count - value2)
            {
                value3 = this.LineList.Count - value2;
            }
            this.LineList.RemoveRange(value2, value3);
        }
    }

    public string ReplaceInLine(int value2, int value3, int value4, string text, LineEnding lineEnding = LineEnding.CrLf)
    {
        if (value2 == 0 && this.LineList.Count == 0)
        {
            this.LineList.Add(new DocumentLine("", lineEnding));
        }
        else if (value2 >= this.LineList.Count)
        {
            return null;
        }
        return this.LineList[value2].Replace(value3, value4, text);
    }

    public void SplitLine(int value2, int value3, LineEnding lineEnding)
    {
        if (this.LineList.Count != 0)
        {
            if (value2 >= this.LineList.Count)
            {
                value2 = this.LineList.Count - 1;
                value3 = int.MaxValue;
            }
            InsertLine(value2 + 1, this.LineList[value2].Replace(value3, -1, ""), lineEnding);
        }
    }

    public void JoinWithNextLine(int value2)
    {
        if (value2 < LineList.Count - 1)
        {
            string text = LineList[value2 + 1].GetText();
            RemoveLines(value2 + 1, 1);
            LineList[value2].Replace(-1, 0, text);
        }
    }

    public void AppendLine(string text)
    {
        LineList.Add(new DocumentLine(text, LineEnding.CrLf));
    }

    public void Clear()
    {
        LineList.Clear();
    }
}
