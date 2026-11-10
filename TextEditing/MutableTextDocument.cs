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
            // The line break that is made here ends the first half, with the ending that was asked for; the second half keeps the ending the
            // line had, so that the terminators of the lines after the break stay as they were (a file with mixed endings).
            DocumentLine line = this.LineList[lineIndex];
            LineEnding originalEnding = line.Ending;
            InsertLine(lineIndex + 1, line.Replace(offset, -1, ""), originalEnding);
            line.Ending = lineEnding;
        }
    }

    public void JoinWithNextLine(int lineIndex)
    {
        if (lineIndex < LineList.Count - 1)
        {
            DocumentLine next = LineList[lineIndex + 1];
            string text = next.GetText();
            RemoveLines(lineIndex + 1, 1);
            LineList[lineIndex].Replace(-1, 0, text);
            // The line break between the two lines is gone, and the joined line ends where the second one ended.
            LineList[lineIndex].Ending = next.Ending;
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
