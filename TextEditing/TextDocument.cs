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

            public LineChange(bool flag, int value2, int value3)
            {
                this.IsInsert = flag;
                this.Start = value2;
                this.Length = value3;
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
            int num = 0;
            for (int num2 = 0; num2 < _text.Length; num2++)
            {
                if (_text[num2] == '\t')
                {
                    num++;
                }
            }
            _tabCount = num;
        }

        public override string ToString()
        {
            return _text;
        }

        public int GetMaxWidth(int value2)
        {
            return _text.Length + _tabCount * (value2 - 1);
        }

        public string GetText()
        {
            return _text;
        }

        private int ConvertPosition(int value2, int value3, bool flag, out bool flag2)
        {
            flag2 = false;
            int num = 0;
            int num2 = 0;
            while (true)
            {
                if (flag)
                {
                    if (num == value3)
                    {
                        return num2;
                    }
                    if (num > value3)
                    {
                        return num2 - 1;
                    }
                }
                else if (value3 <= num2)
                {
                    return num;
                }
                if (num2 >= _text.Length)
                {
                    break;
                }
                num = ((_text[num2] != '\t') ? (num + 1) : (num + (value2 - num % value2)));
                num2++;
            }
            flag2 = true;
            if (!flag)
            {
                return num;
            }
            return num2;
        }

        public int ColumnToIndex(int value2, int value3, out bool flag)
        {
            return this.ConvertPosition(value2, value3, flag: true, out flag);
        }

        public int IndexToColumn(int value2, int value3, out bool flag)
        {
            return this.ConvertPosition(value2, value3, flag: false, out flag);
        }

        private void OnChanged(bool flag, int value2, int value3)
        {
        }

        public void SetText(string text)
        {
            OnChanged(flag: false, 0, _text.Length);
            _text = text;
            OnChanged(flag: true, 0, _text.Length);
            CountTabs();
        }

        public string Replace(int value2, int value3, string text)
        {
            if (value2 == -1 || value2 > _text.Length)
            {
                value2 = _text.Length;
            }
            if (value3 == -1 || value3 >= _text.Length - value2)
            {
                value3 = _text.Length - value2;
            }
            string result = "";
            if (value3 != 0)
            {
                OnChanged(flag: false, value2, value3);
                result = _text.Substring(value2, value3);
                _text = _text.Remove(value2, value3);
            }
            if (text.Length != 0)
            {
                OnChanged(flag: true, value2, text.Length);
                _text = _text.Insert(value2, text);
            }
            return result;
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

        public TextPosition(int value2, int value3)
        {
            Column = value2;
            Line = value3;
        }

        public TextPosition(TextPosition textPosition)
        {
            Column = textPosition.Column;
            Line = textPosition.Line;
        }

        public static bool operator ==(TextPosition textPosition, TextPosition textPosition2)
        {
            if (textPosition.Column == textPosition2.Column)
            {
                return textPosition.Line == textPosition2.Line;
            }
            return false;
        }

        public static bool operator !=(TextPosition textPosition, TextPosition textPosition2)
        {
            if (textPosition.Column == textPosition2.Column)
            {
                return textPosition.Line != textPosition2.Line;
            }
            return true;
        }

        public static bool operator <(TextPosition textPosition, TextPosition textPosition2)
        {
            if (textPosition.Line < textPosition2.Line)
            {
                return true;
            }
            if (textPosition.Line > textPosition2.Line)
            {
                return false;
            }
            if (textPosition.Column < textPosition2.Column)
            {
                return true;
            }
            return false;
        }

        public static bool operator >(TextPosition textPosition, TextPosition textPosition2)
        {
            if (textPosition.Line > textPosition2.Line)
            {
                return true;
            }
            if (textPosition.Line < textPosition2.Line)
            {
                return false;
            }
            if (textPosition.Column > textPosition2.Column)
            {
                return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (Line << 16) ^ Column;
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
            {
                return false;
            }
            if (obj == (object)this)
            {
                return true;
            }
            if (!(obj is TextPosition))
            {
                return false;
            }
            return this == ((TextPosition)obj);
        }

        public override string ToString()
        {
            return $"Line {Line}, Char  {Column}";
        }
    }

    public delegate string LineTerminatorProvider(int value2);

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

    public string GetLine(int value2)
    {
        return Lines[value2].GetText();
    }

    public int GetMaxLineWidth(int value2)
    {
        int num = 0;
        foreach (DocumentLine item in _lines)
        {
            int num2 = item.GetMaxWidth(value2);
            if (num2 > num)
            {
                num = num2;
            }
        }
        return num;
    }

    private void OnReset()
    {
    }

    public string GetLineText(int value2)
    {
        if (value2 >= _lines.Count || value2 < 0)
        {
            return "";
        }
        return _lines[value2].GetText();
    }

    public string GetLineTerminator(int value2, string text = "\r\n")
    {
        if (value2 >= _lines.Count || value2 < 0)
        {
            return text;
        }
        return GetLineEndingText(_lines[value2].Ending);
    }

    public int GetLineLengthWithTerminator(int value2)
    {
        if (value2 >= _lines.Count || value2 < 0)
        {
            return 0;
        }
        return _lines[value2].GetText().Length + _lines[value2].EndingLength;
    }

    public bool LoadFromFile(string text)
    {
        try
        {
            FileStream fileStream = new FileStream(text, FileMode.Open, FileAccess.Read);
            using (fileStream)
            {
                byte[] array = new byte[fileStream.Length];
                fileStream.Read(array, 0, (int)fileStream.Length);
                return SetText(Encoding.ASCII.GetString(array));
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected static List<DocumentLine> SplitLines(string text2)
    {
        int num = 0;
        List<DocumentLine> list = new List<DocumentLine>();
        string text;
        while (true)
        {
            int num2 = text2.IndexOfAny(_lineBreakChars, num);
            if (num2 == -1)
            {
                break;
            }
            int num3 = num2;
            LineEnding lineEnding;
            if (text2[num3] == '\r')
            {
                if (num3 < text2.Length - 1 && text2[num3 + 1] == '\n')
                {
                    num3++;
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
            text = text2.Substring(num, num2 - num);
            list.Add(new DocumentLine(text, lineEnding));
            num = num3 + 1;
        }
        text = text2.Substring(num);
        list.Add(new DocumentLine(text, (list.Count == 0) ? LineEnding.Inherit : list[list.Count - 1].Ending));
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

    public string GetRangeText(TextPosition textPosition, TextPosition textPosition2, LineTerminatorProvider callback = null)
    {
        TextPosition textPosition3;
        TextPosition textPosition4;
        if (textPosition < textPosition2)
        {
            textPosition3 = textPosition;
            textPosition4 = textPosition2;
        }
        else
        {
            textPosition3 = textPosition2;
            textPosition4 = textPosition;
        }
        if (textPosition3.Line == textPosition4.Line)
        {
            return _lines[textPosition3.Line].GetText().Substring(textPosition3.Column, textPosition4.Column - textPosition3.Column);
        }
        StringBuilder stringBuilder = new StringBuilder();
        stringBuilder.Append(_lines[textPosition3.Line].GetText().Substring(textPosition3.Column) + (callback?.Invoke(textPosition3.Line) ?? "\r\n"));
        int num = Math.Min(textPosition4.Line, _lines.Count);
        for (int num2 = textPosition3.Line + 1; num2 < num; num2++)
        {
            stringBuilder.Append(_lines[num2].GetText() + (callback?.Invoke(num2) ?? "\r\n"));
        }
        if (textPosition4.Line < _lines.Count)
        {
            stringBuilder.Append(_lines[textPosition4.Line].GetText().Substring(0, textPosition4.Column));
        }
        return stringBuilder.ToString();
    }

    public static string SubstringFromColumn(string text, int value2, int value3, out int value4, out int value5)
    {
        value4 = 0;
        value5 = 0;
        if (value2 == 0)
        {
            return text;
        }
        int num = 0;
        int num2 = 0;
        for (; num < text.Length; num++)
        {
            if (num2 >= value2)
            {
                value4 = num2 - value2;
                value5 = num;
                return text.Substring(num);
            }
            num2 = ((text[num] != '\t') ? (num2 + 1) : (num2 + (value3 - num2 % value3)));
        }
        return null;
    }

    public string GetLineSubstringFromColumn(int value2, int value3, int value4, out int value5, out int value6)
    {
        return SubstringFromColumn(Lines[value2].GetText(), value3, value4, out value5, out value6);
    }

    public int IndexToColumn(int value2, int value3, int value4)
    {
        bool flag;
        return IndexToColumn(value2, value3, value4, out flag);
    }

    public int IndexToColumn(int value2, int value3, int value4, out bool flag)
    {
        flag = true;
        if (value2 >= Lines.Count || value2 < 0)
        {
            return 0;
        }
        return Lines[value2].IndexToColumn(value4, value3, out flag);
    }

    public int ColumnToIndex(int value2, int value3, int value4)
    {
        bool flag;
        return ColumnToIndex(value2, value3, value4, out flag);
    }

    public int ColumnToIndex(int value2, int value3, int value4, out bool flag)
    {
        flag = true;
        if (value2 >= Lines.Count)
        {
            return 0;
        }
        return Lines[value2].ColumnToIndex(value4, value3, out flag);
    }

    public TextPosition OffsetToPosition(int value2)
    {
        int num = 0;
        for (int num2 = 0; num2 < Lines.Count; num2++)
        {
            int num3 = Lines[num2].GetText().Length + ((Lines[num2].Ending != LineEnding.CrLf) ? 1 : 2);
            int num4 = num + num3;
            if (num4 > value2)
            {
                return new TextPosition(value2 - num, num2);
            }
            num = num4;
        }
        return default;
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
        for (int num = 0; num < _lines.Count; num++)
        {
            DocumentLine documentLine = _lines[num];
            stringBuilder.Append(documentLine.GetText());
            if (num != _lines.Count - 1)
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
