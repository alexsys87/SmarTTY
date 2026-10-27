using System.Collections.Generic;

namespace SmarTTY.TextEditing;

public class MutableTextDocument : TextDocument
{
    private List<DocumentLine> LineList => Lines;

    protected void InsertLine(int lineIndex, string text, LineEnding lineEnding)
    {
        this.LineList.Insert(lineIndex, new DocumentLine(text, lineEnding));
    }

    public void RemoveLines(int lineIndex, int count)
    {
        if (lineIndex >= 0 && lineIndex < this.LineList.Count)
        {
            if (count > this.LineList.Count - lineIndex)
            {
                count = this.LineList.Count - lineIndex;
            }
            this.LineList.RemoveRange(lineIndex, count);
        }
    }

    public string ReplaceInLine(int lineIndex, int offset, int length, string text, LineEnding lineEnding = LineEnding.CrLf)
    {
        if (lineIndex == 0 && this.LineList.Count == 0)
        {
            this.LineList.Add(new DocumentLine("", lineEnding));
        }
        else if (lineIndex >= this.LineList.Count)
        {
            return null;
        }
        return this.LineList[lineIndex].Replace(offset, length, text);
    }

    public void SplitLine(int lineIndex, int offset, LineEnding lineEnding)
    {
        if (this.LineList.Count != 0)
        {
            if (lineIndex >= this.LineList.Count)
            {
                lineIndex = this.LineList.Count - 1;
                offset = int.MaxValue;
            }
            InsertLine(lineIndex + 1, this.LineList[lineIndex].Replace(offset, -1, ""), lineEnding);
        }
    }

    public void JoinWithNextLine(int lineIndex)
    {
        if (lineIndex < LineList.Count - 1)
        {
            string text = LineList[lineIndex + 1].GetText();
            RemoveLines(lineIndex + 1, 1);
            LineList[lineIndex].Replace(-1, 0, text);
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
