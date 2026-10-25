using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
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

        public void Paint(Graphics graphics, Rectangle rectangle, SizeF charSize, int firstLineIndex, int lastLineIndex, int rowCount, ReadonlyTextBrowser readonlyTextBrowser)
        {
            float yPos = rectangle.Top;
            for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
            {
                int lineIndex = readonlyTextBrowser.DisplayRows[rowIdx].Line;
                GutterState gutterState = GutterState.Plain;
                if (readonlyTextBrowser.DisplayRows[rowIdx].VirtualLine == null && lineIndex != -1)
                {
                    gutterState = this._manager.GetGutterState(lineIndex);
                }
                int yOffset = ((int)charSize.Height - 10) / 2;
                int xCenter = rectangle.Left + 1 + 5;
                switch (gutterState)
                {
                    case GutterState.Plain:
                    case GutterState.SectionEnd:
                        graphics.DrawLine(Pens.Black, xCenter, yPos, xCenter, yPos + charSize.Height);
                        if (gutterState == GutterState.SectionEnd)
                        {
                            graphics.DrawLine(Pens.Black, xCenter, yPos + charSize.Height / 2f, rectangle.Right, yPos + charSize.Height / 2f);
                        }
                        break;
                    case GutterState.Expandable:
                    case GutterState.Collapsed:
                        graphics.DrawLine(Pens.Black, xCenter, yPos, xCenter, yPos + (float)yOffset);
                        graphics.DrawRectangle(Pens.Black, xCenter - 5, yPos + (float)yOffset, 10f, 10f);
                        graphics.DrawLine(Pens.Black, xCenter, yPos + (float)yOffset + 10f, xCenter, yPos + charSize.Height);
                        graphics.DrawLine(Pens.Black, xCenter - 3, yPos + (float)yOffset + 5f, xCenter + 3, yPos + (float)yOffset + 5f);
                        if (gutterState == GutterState.Collapsed)
                        {
                            graphics.DrawLine(Pens.Black, xCenter, yPos + (float)yOffset + 2f, xCenter, yPos + (float)yOffset + 10f - 2f);
                        }
                        break;
                }
                yPos += charSize.Height;
            }
        }

        public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int lineCount)
        {
            return 12;
        }

        public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int firstLineIndex, int lastLineIndex, SizeF charSize, int rowIdx)
        {
            if (readonlyTextBrowser.DisplayRows[rowIdx].VirtualLine == null && this._manager.ToggleFold(readonlyTextBrowser.DisplayRows[rowIdx].Line, readonlyTextBrowser, out var endToken, collapseOnly: false))
            {
                if (endToken != null && readonlyTextBrowser.CaretPosition.Line <= endToken.LineNumber && readonlyTextBrowser.CaretPosition.Line >= endToken.Partner.LineNumber)
                {
                    readonlyTextBrowser.SetCaret(new TextDocument.TextPosition(0, endToken.Partner.LineNumber - 1));
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

        public int HiddenLineCount => StartToken.Style.EndsBeforeClosingLine
            ? _hiddenBeforeClosingLine
            : EndToken.LineNumber - StartToken.LineNumber + (_includesPrecedingLine ? 1 : 0) + _trailingBlankLines;

        private readonly int _hiddenBeforeClosingLine;

        /// <summary>The last hidden line; the closing token's line stays visible for a block that ends before it.</summary>
        public int LastHiddenLine => StartToken.LineNumber + HiddenLineCount;

        public int HeaderLine => StartToken.LineNumber - (_includesPrecedingLine ? 1 : 0);

        public int StartLine => StartToken.LineNumber;

        public int EndLine => EndToken.LineNumber;

        public bool EndsBeforeClosingLine => StartToken.Style.EndsBeforeClosingLine;

        public int StartColumn => StartToken.Start;

        public int EndColumn => EndToken.End;

        public FoldedRegion(TokenCache.Token startToken, TokenCache.Token endToken, FoldingManager foldingManager, bool includesPrecedingLine, int trailingBlankLines)
        {
            this._includesPrecedingLine = includesPrecedingLine;
            this._manager = foldingManager;
            StartToken = startToken;
            EndToken = endToken;
            _trailingBlankLines = trailingBlankLines;
            if (startToken.Style.EndsBeforeClosingLine)
            {
                _hiddenBeforeClosingLine = foldingManager.CountHiddenLinesBeforeClosingLine(startToken);
            }
        }

        public override string ToString()
        {
            return $"({StartToken}) - ({EndToken})";
        }

        public string Transform(string lineText, out CharacterRange characterRange, out Brush brush)
        {
            int startCol = 0;
            switch (StartToken.Style.StartBoundary)
            {
                case TokenCache.RegionStyle.FoldBoundary.TokenEnd:
                    startCol = StartToken.End;
                    break;
                case TokenCache.RegionStyle.FoldBoundary.TokenStart:
                    startCol = StartToken.Start;
                    break;
            }
            if (StartToken.Style.EndsBeforeClosingLine)
            {
                // The line of the closing token is not part of the region: only the header line changes.
                string label = StartToken.Style.Text ?? "...";
                characterRange = new CharacterRange(StartToken.End, label.Length);
                brush = Brushes.DarkGray;
                return lineText.Substring(0, Math.Min(StartToken.End, lineText.Length)) + label;
            }
            string endLineText = this._manager._tokenCache.Document.GetLineText(EndToken.LineNumber);
            int endCol = endLineText.Length;
            switch (StartToken.Style.EndBoundary)
            {
                case TokenCache.RegionStyle.FoldBoundary.TokenEnd:
                    endCol = EndToken.Start;
                    break;
                case TokenCache.RegionStyle.FoldBoundary.TokenStart:
                    endCol = EndToken.End;
                    break;
            }
            string labelText = StartToken.Style.Text;
            switch (StartToken.Style.LabelKind)
            {
                case TokenCache.RegionStyle.LabelSource.NextToken:
                    if (StartToken.Index + 1 < StartToken._line.Tokens.Count)
                    {
                        TokenCache.Token nextToken = StartToken._line.Tokens[StartToken.Index + 1];
                        labelText = lineText.Substring(nextToken.Start, nextToken.End - nextToken.Start);
                    }
                    break;
                case TokenCache.RegionStyle.LabelSource.PreviousToken:
                    if (StartToken.Index > 0)
                    {
                        TokenCache.Token prevToken = StartToken._line.Tokens[StartToken.Index - 1];
                        labelText = lineText.Substring(prevToken.Start, prevToken.End - prevToken.Start);
                    }
                    break;
                case TokenCache.RegionStyle.LabelSource.RestOfLine:
                    labelText = lineText.Substring(StartToken.End);
                    break;
            }
            if (this._includesPrecedingLine)
            {
                string headerLineText = this._manager._tokenCache.Document.GetLineText(StartToken.LineNumber);
                string trimmedText = headerLineText.TrimStart(' ', '\t');
                int indentLen = headerLineText.Length - trimmedText.Length;
                characterRange = new CharacterRange(lineText.Length + startCol - indentLen + 1, labelText.Length);
                brush = Brushes.DarkGray;
                return string.Format("{3} {0}{1}{2}", headerLineText.Substring(indentLen, startCol - indentLen), labelText, endLineText.Substring(endCol), lineText);
            }
            characterRange = new CharacterRange(startCol, labelText.Length);
            brush = Brushes.DarkGray;
            return $"{lineText.Substring(0, startCol)}{labelText}{endLineText.Substring(endCol)}";
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

    /// <summary>
    /// Expands every folded region. Needed whenever the tokens are rebuilt: a region refers to tokens of
    /// the previous tokenization. Returns true if anything was folded.
    /// </summary>
    public bool Clear()
    {
        if (_regions.Count == 0)
        {
            return false;
        }
        _regions.Clear();
        return true;
    }

    private GutterState GetGutterState(int lineIndex)
    {
        bool hasPartnerEnd = false;
        if (_regions.ContainsKey(lineIndex))
        {
            return GutterState.Collapsed;
        }
        if (lineIndex >= _tokenCache.LineCount)
        {
            return GutterState.Plain;
        }
        if (lineIndex < _tokenCache.LineCount - 1 && !IsBlankLine(lineIndex))
        {
            List<TokenCache.Token> nextTokens = _tokenCache.GetLineTokens(lineIndex + 1);
            if (nextTokens.Count > 0)
            {
                TokenCache.Token firstToken = nextTokens[0];
                if (firstToken.Style != null && firstToken.Style.OpeningOnNextLine && firstToken.IsOpening && IsFoldable(firstToken) && StartsLine(firstToken))
                {
                    return GutterState.Expandable;
                }
            }
        }
        foreach (TokenCache.Token token in _tokenCache.GetLineTokens(lineIndex))
        {
            if (token.Style != null && token.IsOpening && IsFoldable(token))
            {
                // An opening token alone on its line ("{" in Allman style) is folded from the line above.
                if (!token.Style.OpeningOnNextLine || !StartsLine(token))
                {
                    return GutterState.Expandable;
                }
            }
            else if (!token.IsOpening && token.Partner != null && token.Partner.LineNumber != token.LineNumber && token.Partner.Style != null)
            {
                hasPartnerEnd = true;
            }
        }
        if (hasPartnerEnd)
        {
            return GutterState.SectionEnd;
        }
        return GutterState.Plain;
    }

    /// <summary>Whether the opening token has a partner on another line and the region hides at least one line.</summary>
    private bool IsFoldable(TokenCache.Token token)
    {
        if (token.Partner == null || token.Partner.LineNumber == token.LineNumber)
        {
            return false;
        }
        return !token.Style.EndsBeforeClosingLine || CountHiddenLinesBeforeClosingLine(token) > 0;
    }

    /// <summary>
    /// For a block that ends before its closing token's line: the lines between the opening and the closing token,
    /// except the blank lines right before the closing token.
    /// </summary>
    internal int CountHiddenLinesBeforeClosingLine(TokenCache.Token opening)
    {
        int end = opening.Partner.LineNumber;
        int hidden = end - opening.LineNumber - 1;
        for (int line = end - 1; line > opening.LineNumber && hidden > 0 && IsBlankLine(line); line--)
        {
            hidden--;
        }
        return Math.Max(0, hidden);
    }

    private bool IsBlankLine(int lineIndex)
    {
        return _tokenCache.Document.GetLineText(lineIndex).Trim(' ', '\t').Length == 0;
    }

    /// <summary>
    /// Whether the token is the first text of its line. Tokenizers do not emit tokens for plain text, so the
    /// first token of a line is not necessarily at its start.
    /// </summary>
    private bool StartsLine(TokenCache.Token token)
    {
        string text = _tokenCache.Document.GetLineText(token.LineNumber);
        int index = 0;
        while (index < token.Start && index < text.Length && (text[index] == ' ' || text[index] == '\t'))
        {
            index++;
        }
        return index == token.Start;
    }

    private int CountTrailingBlankLines(int lineIndex, int maxCount)
    {
        for (int i = 0; i <= maxCount; i++)
        {
            int nextLine = lineIndex + 1;
            if (nextLine >= _tokenCache.LineCount)
            {
                return i;
            }
            if (_tokenCache.Document.GetLineText(nextLine).Trim(' ', '\t').Length != 0)
            {
                return i;
            }
        }
        return maxCount;
    }

    private bool ToggleFold(int lineIndex, ReadonlyTextBrowser readonlyTextBrowser, out TokenCache.Token endToken, bool collapseOnly)
    {
        endToken = null;
        if (lineIndex >= _tokenCache.LineCount)
        {
            return false;
        }
        if (_regions.ContainsKey(lineIndex))
        {
            if (collapseOnly)
            {
                return false;
            }
            _regions.Remove(lineIndex);
            return true;
        }
        if (lineIndex < _tokenCache.LineCount - 1 && !IsBlankLine(lineIndex))
        {
            List<TokenCache.Token> nextTokens = _tokenCache.GetLineTokens(lineIndex + 1);
            if (nextTokens.Count > 0)
            {
                TokenCache.Token firstToken = nextTokens[0];
                if (firstToken.Style != null && firstToken.Style.OpeningOnNextLine && firstToken.IsOpening && IsFoldable(firstToken) && StartsLine(firstToken))
                {
                    _regions[lineIndex] = new FoldedRegion(firstToken, firstToken.Partner, this, includesPrecedingLine: true, this.CountTrailingBlankLines(firstToken.Partner.LineNumber, firstToken.Style.TrailingBlankLines));
                    endToken = firstToken.Partner;
                    return true;
                }
            }
        }
        foreach (TokenCache.Token token in _tokenCache.GetLineTokens(lineIndex))
        {
            if (token.Style != null && token.IsOpening && IsFoldable(token))
            {
                _regions[lineIndex] = new FoldedRegion(token, token.Partner, this, includesPrecedingLine: false, this.CountTrailingBlankLines(token.Partner.LineNumber, token.Style.TrailingBlankLines));
                endToken = token.Partner;
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
        int hiddenCount = 0;
        foreach (FoldedRegion region in _regions.Values)
        {
            hiddenCount += region.HiddenLineCount;
        }
        return hiddenCount;
    }

    public int GetNextSpecialLine(int lineIndex)
    {
        int minLine = int.MaxValue;
        foreach (int key in _regions.Keys)
        {
            if (key >= lineIndex && key < minLine)
            {
                minLine = key;
            }
        }
        return minLine;
    }

    public int GetVirtualRowCount(int lineIndex)
    {
        return 0;
    }

    public void GetVirtualLines(int lineIndex, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
    }

    public int GetHiddenLineCount(int lineIndex)
    {
        if (!_regions.TryGetValue(lineIndex, out var region))
        {
            return 0;
        }
        return region.HiddenLineCount;
    }

    public void RemoveStaleRegions(int startLineIndex, int oldLineCount, int newLineCount, out bool modified)
    {
        Dictionary<int, FoldedRegion> relocated = new Dictionary<int, FoldedRegion>();
        List<int> toDelete = new List<int>();
        foreach (KeyValuePair<int, FoldedRegion> item in _regions)
        {
            if (item.Value.HeaderLine == -1 || item.Value.EndToken.LineNumber == -1)
            {
                toDelete.Add(item.Key);
            }
            else if (item.Value.HeaderLine != item.Key)
            {
                relocated[item.Key] = item.Value;
            }
        }
        foreach (int key in toDelete)
        {
            _regions.Remove(key);
        }
        foreach (int key in relocated.Keys)
        {
            _regions.Remove(key);
        }
        foreach (FoldedRegion region in relocated.Values)
        {
            _regions[region.HeaderLine] = region;
        }
        modified = toDelete.Count != 0 || relocated.Count != 0;
    }

    public ReadonlyTextBrowser.ILineDecorator GetDecorator(int lineIndex)
    {
        if (!_regions.TryGetValue(lineIndex, out var region))
        {
            return null;
        }
        return region;
    }

    internal bool ExpandAt(int lineIndex, int columnIndex)
    {
        List<int> toExpand = new List<int>();
        foreach (KeyValuePair<int, FoldedRegion> item in _regions)
        {
            if (item.Value.EndsBeforeClosingLine)
            {
                // The caret is in a hidden line or on the "..." of the header line.
                if ((item.Value.StartLine < lineIndex && lineIndex <= item.Value.LastHiddenLine) || (item.Value.StartLine == lineIndex && item.Value.StartColumn <= columnIndex) || (columnIndex == -1 && item.Value.HeaderLine == lineIndex))
                {
                    toExpand.Add(item.Key);
                }
            }
            else if ((item.Value.StartLine < lineIndex || (item.Value.StartLine == lineIndex && item.Value.StartColumn <= columnIndex)) && (item.Value.EndLine > lineIndex || (item.Value.EndLine == lineIndex && item.Value.EndColumn >= columnIndex)))
            {
                toExpand.Add(item.Key);
            }
            else if (columnIndex == -1 && item.Value.HeaderLine == lineIndex)
            {
                toExpand.Add(item.Key);
            }
        }
        if (toExpand.Count != 0)
        {
            foreach (int key in toExpand)
            {
                _regions.Remove(key);
            }
            return true;
        }
        return false;
    }

    public bool OnCaretMoved(int lineIndex, int columnIndex)
    {
        return ExpandAt(lineIndex, columnIndex);
    }

    public bool ExpandLine(int lineIndex)
    {
        return ExpandAt(lineIndex, -1);
    }

    public bool CollapseLine(int lineIndex, ReadonlyTextBrowser readonlyTextBrowser)
    {
        TokenCache.Token endToken;
        return ToggleFold(lineIndex, readonlyTextBrowser, out endToken, collapseOnly: true);
    }

    public FoldedLineInfo[] GetCollapsedRegions()
    {
        List<FoldedLineInfo> items = new List<FoldedLineInfo>();
        foreach (FoldedRegion region in _regions.Values)
        {
            items.Add(new FoldedLineInfo
            {
                Line = region.HeaderLine,
                Text = _tokenCache.Document.GetLineText(region.HeaderLine)
            });
        }
        return items.ToArray();
    }
}
