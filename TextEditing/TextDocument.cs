using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SmarTTY.TextEditing;

[DefaultMember("Item")]
public class TextDocument : ILineCollection, IEnumerable<string>, IEnumerable, ITextSource
{
    public enum LineEnding
    {
        Cr,
        Lf,
        CrLf,
        Inherit
    }

    protected class DocumentLine
    {
        public class LineChange
        {
            public readonly bool IsInsert;

            public int Start;

            public int Length;

            public LineChange(bool isInsert, int start, int length)
            {
                this.IsInsert = isInsert;
                this.Start = start;
                this.Length = length;
            }
        }

        private string _text;

        public LineEnding Ending;

        private int _tabCount;

        public int EndingLength
        {
            get
            {
                switch (Ending)
                {
                case LineEnding.Cr:
                case LineEnding.Lf:
                    return 1;
                case LineEnding.CrLf:
                    return 2;
                default:
                    return 0;
                }
            }
        }

        public DocumentLine(string text, LineEnding lineEnding)
        {
            _text = text;
            Ending = lineEnding;
            CountTabs();
        }

        private void CountTabs()
        {
            int tabs = 0;
            for (int i = 0; i < _text.Length; i++)
            {
                if (_text[i] == '\t')
                {
                    tabs++;
                }
            }
            _tabCount = tabs;
        }

        public override string ToString()
        {
            return _text;
        }

        public int GetMaxWidth(int tabSize)
        {
            return _text.Length + _tabCount * (tabSize - 1);
        }

        public string GetText()
        {
            return _text;
        }

        private int ConvertPosition(int tabSize, int target, bool isColumnToIndex, out bool isPastEnd)
        {
            isPastEnd = false;
            int currCol = 0;
            int currIdx = 0;
            while (true)
            {
                if (isColumnToIndex)
                {
                    if (currCol == target)
                    {
                        return currIdx;
                    }
                    if (currCol > target)
                    {
                        return currIdx - 1;
                    }
                }
                else if (target <= currIdx)
                {
                    return currCol;
                }
                if (currIdx >= _text.Length)
                {
                    break;
                }
                currCol = ((_text[currIdx] != '\t') ? (currCol + 1) : (currCol + (tabSize - currCol % tabSize)));
                currIdx++;
            }
            isPastEnd = true;
            if (!isColumnToIndex)
            {
                return currCol;
            }
            return currIdx;
        }

        public int ColumnToIndex(int tabSize, int column, out bool isPastEnd)
        {
            return this.ConvertPosition(tabSize, column, isColumnToIndex: true, out isPastEnd);
        }

        public int IndexToColumn(int tabSize, int index, out bool isPastEnd)
        {
            return this.ConvertPosition(tabSize, index, isColumnToIndex: false, out isPastEnd);
        }

        private void OnChanged(bool isInsert, int start, int length)
        {
        }

        public void SetText(string text)
        {
            OnChanged(isInsert: false, 0, _text.Length);
            _text = text;
            OnChanged(isInsert: true, 0, _text.Length);
            CountTabs();
        }

        public string Replace(int offset, int length, string newText)
        {
            if (offset == -1 || offset > _text.Length)
            {
                offset = _text.Length;
            }
            if (length == -1 || length >= _text.Length - offset)
            {
                length = _text.Length - offset;
            }
            string text = "";
            if (length != 0)
            {
                OnChanged(isInsert: false, offset, length);
                text = _text.Substring(offset, length);
                _text = _text.Remove(offset, length);
            }
            if (newText.Length != 0)
            {
                OnChanged(isInsert: true, offset, newText.Length);
                _text = _text.Insert(offset, newText);
            }
            return text;
        }
    }

    public struct TextPosition
    {
        public int Column;

        public int Line;

        public static readonly TextPosition Invalid = new TextPosition
        {
            Column = -1,
            Line = -1
        };

        public bool IsValid
        {
            get
            {
                if (Line != -1)
                {
                    return Column != -1;
                }
                return false;
            }
        }

        public int LastFullLine
        {
            get
            {
                if (Column == 0 && Line > 0)
                {
                    return Line - 1;
                }
                return Line;
            }
        }

        public TextPosition(int column, int line)
        {
            Column = column;
            Line = line;
        }

        public TextPosition(TextPosition textPosition)
        {
            Column = textPosition.Column;
            Line = textPosition.Line;
        }

        public static bool operator ==(TextPosition left, TextPosition right)
        {
            if (left.Column == right.Column)
            {
                return left.Line == right.Line;
            }
            return false;
        }

        public static bool operator !=(TextPosition left, TextPosition right)
        {
            if (left.Column == right.Column)
            {
                return left.Line != right.Line;
            }
            return true;
        }

        public static bool operator <(TextPosition left, TextPosition right)
        {
            if (left.Line < right.Line)
            {
                return true;
            }
            if (left.Line > right.Line)
            {
                return false;
            }
            if (left.Column < right.Column)
            {
                return true;
            }
            return false;
        }

        public static bool operator >(TextPosition left, TextPosition right)
        {
            if (left.Line > right.Line)
            {
                return true;
            }
            if (left.Line < right.Line)
            {
                return false;
            }
            if (left.Column > right.Column)
            {
                return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (Line << 16) ^ Column;
        }

        public override bool Equals(object sender)
        {
            if (sender == null)
            {
                return false;
            }
            if (sender == (object)this)
            {
                return true;
            }
            if (!(sender is TextPosition))
            {
                return false;
            }
            return this == ((TextPosition)sender);
        }

        public override string ToString()
        {
            return $"Line {Line}, Char  {Column}";
        }
    }

    public delegate string LineTerminatorProvider(int lineIndex);

    private class LineEnumerator : IEnumerator<string>, IDisposable, IEnumerator
    {
        private TextDocument _document;

        private int _index = -1;

        public string Current
        {
            get
            {
                if (_index != -1)
                {
                    return _document.GetLineText(_index);
                }
                return null;
            }
        }

        private object CurrentObject => _document.GetLineText(_index);

        object IEnumerator.Current => CurrentObject;

        public LineEnumerator(TextDocument textDocument)
        {
            _document = textDocument;
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            if (_index >= _document.LineCount - 1)
            {
                return false;
            }
            _index++;
            return true;
        }

        public void Reset()
        {
            _index = -1;
        }
    }

    public LineEnding DefaultLineEnding = LineEnding.CrLf;

    private List<DocumentLine> _lines = new List<DocumentLine>();

    protected static readonly char[] _lineBreakChars = new char[2] { '\r', '\n' };

    protected List<DocumentLine> Lines => _lines;

    public int LineCount => _lines.Count;

    private ILineCollection AsLineCollection => this;

    ILineCollection ITextSource.Lines => AsLineCollection;

    public string GetLine(int lineIndex)
    {
        return Lines[lineIndex].GetText();
    }

    public int GetMaxLineWidth(int tabSize)
    {
        int maxWidth = 0;
        foreach (DocumentLine line in _lines)
        {
            int width = line.GetMaxWidth(tabSize);
            if (width > maxWidth)
            {
                maxWidth = width;
            }
        }
        return maxWidth;
    }

    private void OnReset()
    {
    }

    public string GetLineText(int lineIndex)
    {
        if (lineIndex >= _lines.Count || lineIndex < 0)
        {
            return "";
        }
        return _lines[lineIndex].GetText();
    }

    public string GetLineTerminator(int lineIndex, string defaultTerminator = "\r\n")
    {
        if (lineIndex >= _lines.Count || lineIndex < 0)
        {
            return defaultTerminator;
        }
        return GetLineEndingText(_lines[lineIndex].Ending);
    }

    /// <summary>
    /// Returns the terminator that ends the line in the saved text: a line with an inherited ending
    /// (e.g. the only line of an empty file) or a missing line uses <see cref="DefaultLineEnding"/>.
    /// </summary>
    public string GetEffectiveLineTerminator(int lineIndex)
    {
        LineEnding lineEnding = (lineIndex >= 0 && lineIndex < _lines.Count) ? _lines[lineIndex].Ending : LineEnding.Inherit;
        if (lineEnding == LineEnding.Inherit)
        {
            lineEnding = DefaultLineEnding;
        }
        return GetLineEndingText(lineEnding);
    }

    public int GetLineLengthWithTerminator(int lineIndex)
    {
        if (lineIndex >= _lines.Count || lineIndex < 0)
        {
            return 0;
        }
        return _lines[lineIndex].GetText().Length + _lines[lineIndex].EndingLength;
    }

    public bool LoadFromFile(string filePath)
    {
        try
        {
            FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using (fileStream)
            {
                byte[] buffer = new byte[fileStream.Length];
                fileStream.Read(buffer, 0, (int)fileStream.Length);
                return SetText(Encoding.ASCII.GetString(buffer));
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected static List<DocumentLine> SplitLines(string text)
    {
        int startIndex = 0;
        List<DocumentLine> list = new List<DocumentLine>();
        string lineText;
        while (true)
        {
            int breakIndex = text.IndexOfAny(_lineBreakChars, startIndex);
            if (breakIndex == -1)
            {
                break;
            }
            int endingEndIndex = breakIndex;
            LineEnding lineEnding;
            if (text[endingEndIndex] == '\r')
            {
                if (endingEndIndex < text.Length - 1 && text[endingEndIndex + 1] == '\n')
                {
                    endingEndIndex++;
                    lineEnding = LineEnding.CrLf;
                }
                else
                {
                    lineEnding = LineEnding.Cr;
                }
            }
            else
            {
                lineEnding = LineEnding.Lf;
            }
            lineText = text.Substring(startIndex, breakIndex - startIndex);
            list.Add(new DocumentLine(lineText, lineEnding));
            startIndex = endingEndIndex + 1;
        }
        lineText = text.Substring(startIndex);
        list.Add(new DocumentLine(lineText, (list.Count == 0) ? LineEnding.Inherit : list[list.Count - 1].Ending));
        return list;
    }

    public bool SetText(string text)
    {
        OnReset();
        _lines = SplitLines(text);
        return true;
    }

    public override string ToString()
    {
        return GetText();
    }

    public string GetRangeText(TextPosition startPos, TextPosition endPos, LineTerminatorProvider callback = null)
    {
        TextPosition minPos;
        TextPosition maxPos;
        if (startPos < endPos)
        {
            minPos = startPos;
            maxPos = endPos;
        }
        else
        {
            minPos = endPos;
            maxPos = startPos;
        }
        if (minPos.Line == maxPos.Line)
        {
            return _lines[minPos.Line].GetText().Substring(minPos.Column, maxPos.Column - minPos.Column);
        }
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(_lines[minPos.Line].GetText().Substring(minPos.Column) + (callback?.Invoke(minPos.Line) ?? "\r\n"));
        int endLine = Math.Min(maxPos.Line, _lines.Count);
        for (int i = minPos.Line + 1; i < endLine; i++)
        {
            stringBuilder.Append(_lines[i].GetText() + (callback?.Invoke(i) ?? "\r\n"));
        }
        if (maxPos.Line < _lines.Count)
        {
            stringBuilder.Append(_lines[maxPos.Line].GetText().Substring(0, maxPos.Column));
        }
        return stringBuilder.ToString();
    }

    public static string SubstringFromColumn(string lineText, int targetColumn, int tabSize, out int columnOffset, out int charIndex)
    {
        columnOffset = 0;
        charIndex = 0;
        if (targetColumn == 0)
        {
            return lineText;
        }
        int i = 0;
        int currCol = 0;
        for (; i < lineText.Length; i++)
        {
            if (currCol >= targetColumn)
            {
                columnOffset = currCol - targetColumn;
                charIndex = i;
                return lineText.Substring(i);
            }
            currCol = ((lineText[i] != '\t') ? (currCol + 1) : (currCol + (tabSize - currCol % tabSize)));
        }
        return null;
    }

    public int IndexToColumn(int lineIndex, int index, int tabSize)
    {
        bool isPastEnd;
        return IndexToColumn(lineIndex, index, tabSize, out isPastEnd);
    }

    public int IndexToColumn(int lineIndex, int index, int tabSize, out bool isPastEnd)
    {
        isPastEnd = true;
        if (lineIndex >= Lines.Count || lineIndex < 0)
        {
            return 0;
        }
        return Lines[lineIndex].IndexToColumn(tabSize, index, out isPastEnd);
    }

    public int ColumnToIndex(int lineIndex, int column, int tabSize)
    {
        bool isPastEnd;
        return ColumnToIndex(lineIndex, column, tabSize, out isPastEnd);
    }

    public int ColumnToIndex(int lineIndex, int column, int tabSize, out bool isPastEnd)
    {
        isPastEnd = true;
        if (lineIndex >= Lines.Count)
        {
            return 0;
        }
        return Lines[lineIndex].ColumnToIndex(tabSize, column, out isPastEnd);
    }

    public TextPosition OffsetToPosition(int offset)
    {
        int currOffset = 0;
        for (int i = 0; i < Lines.Count; i++)
        {
            int lineLen = Lines[i].GetText().Length + ((Lines[i].Ending != LineEnding.CrLf) ? 1 : 2);
            int nextOffset = currOffset + lineLen;
            if (nextOffset > offset)
            {
                return new TextPosition(offset - currOffset, i);
            }
            currOffset = nextOffset;
        }
        return default;
    }

    /// <summary>
    /// Replaces every non-overlapping occurrence of <paramref name="searchText"/> (left to right) and adds the
    /// number of replacements to <paramref name="count"/>. Returns <paramref name="text"/> itself when nothing matched.
    /// </summary>
    public static string ReplaceAll(string text, string searchText, string replacement, StringComparison comparison, ref int count)
    {
        int index = string.IsNullOrEmpty(searchText) ? -1 : text.IndexOf(searchText, comparison);
        if (index < 0)
        {
            return text;
        }
        StringBuilder result = new StringBuilder();
        int last = 0;
        while (index >= 0)
        {
            result.Append(text, last, index - last).Append(replacement);
            count++;
            last = index + searchText.Length;
            index = text.IndexOf(searchText, last, comparison);
        }
        result.Append(text, last, text.Length - last);
        return result.ToString();
    }

    public static string GetLineEndingText(LineEnding lineEnding)
    {
        return lineEnding switch
        {
            LineEnding.Cr => "\r",
            LineEnding.Lf => "\n",
            LineEnding.CrLf => "\r\n",
            _ => "",
        };
    }

    public string GetText()
    {
        StringBuilder stringBuilder = new StringBuilder(_lines.Count * 80);
        for (int i = 0; i < _lines.Count; i++)
        {
            DocumentLine documentLine = _lines[i];
            stringBuilder.Append(documentLine.GetText());
            if (i != _lines.Count - 1)
            {
                LineEnding lineEnding = documentLine.Ending;
                if (lineEnding == LineEnding.Inherit)
                {
                    lineEnding = DefaultLineEnding;
                }
                stringBuilder.Append(GetLineEndingText(lineEnding));
            }
        }
        return stringBuilder.ToString();
    }

    public IEnumerator<string> GetEnumerator()
    {
        return new LineEnumerator(this);
    }

    private IEnumerator GetEnumeratorNonGeneric()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return this.GetEnumeratorNonGeneric();
    }
}
