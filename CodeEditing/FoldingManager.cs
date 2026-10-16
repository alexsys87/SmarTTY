using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System.Collections.Generic;
using System.Drawing;

namespace SmarTTY.CodeEditing;

internal class FoldingManager : ReadonlyTextBrowser.IVirtualLineProvider
{
    internal class FoldingMargin : IMargin
    {

        private FoldingManager _manager;

        public FoldingMargin(FoldingManager foldingManager)
        {
            _manager = foldingManager;
        }

        public void Paint(Graphics graphics, Rectangle rectangle, SizeF sizeF, int value2, int value3, int value4, ReadonlyTextBrowser readonlyTextBrowser)
        {
            float num = rectangle.Top;
            for (int num2 = 0; num2 < value4; num2++)
            {
                int num3 = readonlyTextBrowser.DisplayRows[num2].Line;
                GutterState gutterState = GutterState.Plain;
                if (readonlyTextBrowser.DisplayRows[num2].VirtualLine == null && num3 != -1)
                {
                    gutterState = this._manager.GetGutterState(num3);
                }
                int num4 = ((int)sizeF.Height - 10) / 2;
                int num5 = rectangle.Left + 1 + 5;
                switch (gutterState)
                {
                    case GutterState.Plain:
                    case GutterState.SectionEnd:
                        graphics.DrawLine(Pens.Black, num5, num, num5, num + sizeF.Height);
                        if (gutterState == GutterState.SectionEnd)
                        {
                            graphics.DrawLine(Pens.Black, num5, num + sizeF.Height / 2f, rectangle.Right, num + sizeF.Height / 2f);
                        }
                        break;
                    case GutterState.Expandable:
                    case GutterState.Collapsed:
                        graphics.DrawLine(Pens.Black, num5, num, num5, num + (float)num4);
                        graphics.DrawRectangle(Pens.Black, num5 - 5, num + (float)num4, 10f, 10f);
                        graphics.DrawLine(Pens.Black, num5, num + (float)num4 + 10f, num5, num + sizeF.Height);
                        graphics.DrawLine(Pens.Black, num5 - 3, num + (float)num4 + 5f, num5 + 3, num + (float)num4 + 5f);
                        if (gutterState == GutterState.Collapsed)
                        {
                            graphics.DrawLine(Pens.Black, num5, num + (float)num4 + 2f, num5, num + (float)num4 + 10f - 2f);
                        }
                        break;
                }
                num += sizeF.Height;
            }
        }

        public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int value2)
        {
            return 12;
        }

        public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, SizeF sizeF, int value4)
        {
            if (readonlyTextBrowser.DisplayRows[value4].VirtualLine == null && this._manager.ToggleFold(readonlyTextBrowser.DisplayRows[value4].Line, readonlyTextBrowser, out var token, flag: false))
            {
                if (token != null && readonlyTextBrowser.CaretPosition.Line <= token.LineNumber && readonlyTextBrowser.CaretPosition.Line >= token.Partner.LineNumber)
                {
                    readonlyTextBrowser.SetCaret(new TextDocument.TextPosition(0, token.Partner.LineNumber - 1));
                }
                readonlyTextBrowser.RebuildDisplayRows();
                readonlyTextBrowser.UpdateLayout();
                readonlyTextBrowser.Redraw();
            }
            return true;
        }
    }

    private enum GutterState
    {
        Plain,
        Expandable,
        Collapsed,
        SectionEnd
    }

    private class FoldedRegion : ReadonlyTextBrowser.ILineDecorator
    {
        private FoldingManager _manager;

        public readonly TokenCache.Token StartToken;

        public readonly TokenCache.Token EndToken;

        private bool _includesPrecedingLine;

        private int _trailingBlankLines;

        public int HiddenLineCount => EndToken.LineNumber - StartToken.LineNumber + (_includesPrecedingLine ? 1 : 0) + _trailingBlankLines;

        public int HeaderLine => StartToken.LineNumber - (_includesPrecedingLine ? 1 : 0);

        public int StartLine => StartToken.LineNumber;

        public int EndLine => EndToken.LineNumber;

        public int StartColumn => StartToken.Start;

        public int EndColumn => EndToken.End;

        public FoldedRegion(TokenCache.Token token, TokenCache.Token token2, FoldingManager foldingManager, bool flag, int value2)
        {
            this._includesPrecedingLine = flag;
            this._manager = foldingManager;
            StartToken = token;
            EndToken = token2;
            _trailingBlankLines = value2;
        }

        public override string ToString()
        {
            return $"({StartToken}) - ({EndToken})";
        }

        public string Transform(string text5, out CharacterRange characterRange, out Brush brush)
        {
            int num = 0;
            switch (StartToken.Style.StartBoundary)
            {
                case TokenCache.RegionStyle.FoldBoundary.TokenEnd:
                    num = StartToken.End;
                    break;
                case TokenCache.RegionStyle.FoldBoundary.TokenStart:
                    num = StartToken.Start;
                    break;
            }
            string text = this._manager._tokenCache.Document.GetLineText(EndToken.LineNumber);
            int length = text.Length;
            switch (StartToken.Style.EndBoundary)
            {
                case TokenCache.RegionStyle.FoldBoundary.TokenEnd:
                    length = EndToken.Start;
                    break;
                case TokenCache.RegionStyle.FoldBoundary.TokenStart:
                    length = EndToken.End;
                    break;
            }
            string text2 = StartToken.Style.Text;
            switch (StartToken.Style.LabelKind)
            {
                case TokenCache.RegionStyle.LabelSource.NextToken:
                    if (StartToken.Index + 1 < StartToken._line.Tokens.Count)
                    {
                        TokenCache.Token token = StartToken._line.Tokens[StartToken.Index + 1];
                        text2 = text5.Substring(token.Start, token.End - token.Start);
                    }
                    break;
                case TokenCache.RegionStyle.LabelSource.PreviousToken:
                    if (StartToken.Index > 0)
                    {
                        TokenCache.Token token2 = StartToken._line.Tokens[StartToken.Index - 1];
                        text2 = text5.Substring(token2.Start, token2.End - token2.Start);
                    }
                    break;
                case TokenCache.RegionStyle.LabelSource.RestOfLine:
                    text2 = text5.Substring(StartToken.End);
                    break;
            }
            if (this._includesPrecedingLine)
            {
                string text3 = this._manager._tokenCache.Document.GetLineText(StartToken.LineNumber);
                string text4 = text3.TrimStart(' ', '\t');
                int num2 = text3.Length - text4.Length;
                characterRange = new CharacterRange(text5.Length + num - num2 + 1, text2.Length);
                brush = Brushes.DarkGray;
                return string.Format("{3} {0}{1}{2}", text3.Substring(num2, num - num2), text2, text.Substring(length), text5);
            }
            characterRange = new CharacterRange(num, text2.Length);
            brush = Brushes.DarkGray;
            return $"{text5.Substring(0, num)}{text2}{text.Substring(length)}";
        }

        public void DrawDecoration(Graphics graphics, SizeF sizeF, Rectangle rectangle, CharacterRange characterRange)
        {
            graphics.DrawRectangle(Pens.DarkGray, rectangle);
        }

        public TextDocument.TextPosition MapPosition(string text, TextDocument.TextPosition textPosition)
        {
            if (textPosition.Line != HeaderLine)
            {
                return textPosition;
            }
            if (textPosition.Column < text.Length)
            {
                return textPosition;
            }
            return new TextDocument.TextPosition(text.Length, textPosition.Line);
        }
    }

    private TokenCache _tokenCache;

    private Dictionary<int, FoldedRegion> _regions = new Dictionary<int, FoldedRegion>();

    public FoldingManager(TokenCache tokenCache)
    {
        _tokenCache = tokenCache;
    }

    private GutterState GetGutterState(int value2)
    {
        bool flag = false;
        if (_regions.ContainsKey(value2))
        {
            return GutterState.Collapsed;
        }
        if (value2 >= _tokenCache.LineCount)
        {
            return GutterState.Plain;
        }
        if (value2 < _tokenCache.LineCount - 1 && _tokenCache.GetLineTokens(value2).Count != 0)
        {
            List<TokenCache.Token> list = _tokenCache.GetLineTokens(value2 + 1);
            if (list.Count > 0)
            {
                TokenCache.Token token = list[0];
                if (token.Style != null && token.Style.OpeningOnNextLine && token.IsOpening && token.Partner != null && token.Partner.LineNumber != token.LineNumber)
                {
                    return GutterState.Expandable;
                }
            }
        }
        int num = -1;
        foreach (TokenCache.Token item in _tokenCache.GetLineTokens(value2))
        {
            num++;
            if (item.Style != null && item.IsOpening && item.Partner != null && item.Partner.LineNumber != item.LineNumber)
            {
                if (num != 0 || !item.Style.OpeningOnNextLine)
                {
                    return GutterState.Expandable;
                }
            }
            else if (!item.IsOpening && item.Partner != null && item.Partner.LineNumber != item.LineNumber && item.Partner.Style != null)
            {
                flag = true;
            }
        }
        if (flag)
        {
            return GutterState.SectionEnd;
        }
        return GutterState.Plain;
    }

    private int CountTrailingBlankLines(int value2, int value3)
    {
        for (int num = 0; num <= value3; num++)
        {
            int num2 = value2 + 1;
            if (num2 >= _tokenCache.LineCount)
            {
                return num;
            }
            if (_tokenCache.Document.GetLineText(num2).Trim(' ', '\t').Length != 0)
            {
                return num;
            }
        }
        return value3;
    }

    private bool ToggleFold(int value2, ReadonlyTextBrowser readonlyTextBrowser, out TokenCache.Token token, bool flag)
    {
        token = null;
        if (value2 >= _tokenCache.LineCount)
        {
            return false;
        }
        if (_regions.ContainsKey(value2))
        {
            if (flag)
            {
                return false;
            }
            _regions.Remove(value2);
            return true;
        }
        if (value2 < _tokenCache.LineCount - 1 && _tokenCache.GetLineTokens(value2).Count != 0)
        {
            List<TokenCache.Token> list = _tokenCache.GetLineTokens(value2 + 1);
            if (list.Count > 0)
            {
                TokenCache.Token token2 = list[0];
                if (token2.Style != null && token2.Style.OpeningOnNextLine && token2.IsOpening && token2.Partner != null && token2.Partner.LineNumber != token2.LineNumber)
                {
                    _regions[value2] = new FoldedRegion(token2, token2.Partner, this, flag: true, this.CountTrailingBlankLines(token2.Partner.LineNumber, token2.Style.TrailingBlankLines));
                    token = token2.Partner;
                    return true;
                }
            }
        }
        foreach (TokenCache.Token item in _tokenCache.GetLineTokens(value2))
        {
            if (item.Style != null && item.IsOpening && item.Partner != null && item.Partner.LineNumber != item.LineNumber)
            {
                _regions[value2] = new FoldedRegion(item, item.Partner, this, flag: false, this.CountTrailingBlankLines(item.Partner.LineNumber, item.Style.TrailingBlankLines));
                token = item.Partner;
                return true;
            }
        }
        return false;
    }

    public int GetTotalVirtualRows()
    {
        return 0;
    }

    public int GetTotalHiddenLines()
    {
        int num = 0;
        foreach (FoldedRegion value in _regions.Values)
        {
            num += value.HiddenLineCount;
        }
        return num;
    }

    public int GetNextSpecialLine(int value2)
    {
        int num = int.MaxValue;
        foreach (int key in _regions.Keys)
        {
            if (key >= value2 && key < num)
            {
                num = key;
            }
        }
        return num;
    }

    public int GetVirtualRowCount(int value2)
    {
        return 0;
    }

    public void GetVirtualLines(int value2, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
    }

    public int GetHiddenLineCount(int value2)
    {
        if (!_regions.TryGetValue(value2, out var value))
        {
            return 0;
        }
        return value.HiddenLineCount;
    }

    public void RemoveStaleRegions(int value2, int value3, int value4, out bool flag)
    {
        Dictionary<int, FoldedRegion> dictionary = new Dictionary<int, FoldedRegion>();
        List<int> list = new List<int>();
        foreach (KeyValuePair<int, FoldedRegion> item in _regions)
        {
            if (item.Value.HeaderLine == -1 || item.Value.EndToken.LineNumber == -1)
            {
                list.Add(item.Key);
            }
            else if (item.Value.HeaderLine != item.Key)
            {
                dictionary[item.Key] = item.Value;
            }
        }
        foreach (int item2 in list)
        {
            _regions.Remove(item2);
        }
        foreach (int key in dictionary.Keys)
        {
            _regions.Remove(key);
        }
        foreach (FoldedRegion value in dictionary.Values)
        {
            _regions[value.HeaderLine] = value;
        }
        flag = list.Count != 0 || dictionary.Count != 0;
    }

    public ReadonlyTextBrowser.ILineDecorator GetDecorator(int value2)
    {
        if (!_regions.TryGetValue(value2, out var value))
        {
            return null;
        }
        return value;
    }

    internal bool ExpandAt(int value2, int value3)
    {
        List<int> list = new List<int>();
        foreach (KeyValuePair<int, FoldedRegion> item in _regions)
        {
            if ((item.Value.StartLine < value2 || (item.Value.StartLine == value2 && item.Value.StartColumn <= value3)) && (item.Value.EndLine > value2 || (item.Value.EndLine == value2 && item.Value.EndColumn >= value3)))
            {
                list.Add(item.Key);
            }
            else if (value3 == -1 && item.Value.HeaderLine == value2)
            {
                list.Add(item.Key);
            }
        }
        if (list.Count != 0)
        {
            foreach (int item2 in list)
            {
                _regions.Remove(item2);
            }
            return true;
        }
        return false;
    }

    public bool OnCaretMoved(int value2, int value3)
    {
        return ExpandAt(value2, value3);
    }

    public bool ExpandLine(int value2)
    {
        return ExpandAt(value2, -1);
    }

    public bool CollapseLine(int value2, ReadonlyTextBrowser readonlyTextBrowser)
    {
        TokenCache.Token token;
        return ToggleFold(value2, readonlyTextBrowser, out token, flag: true);
    }

    public FoldedLineInfo[] GetCollapsedRegions()
    {
        List<FoldedLineInfo> list = new List<FoldedLineInfo>();
        foreach (FoldedRegion value in _regions.Values)
        {
            list.Add(new FoldedLineInfo
            {
                Line = value.HeaderLine,
                Text = _tokenCache.Document.GetLineText(value.HeaderLine)
            });
        }
        return list.ToArray();
    }
}
