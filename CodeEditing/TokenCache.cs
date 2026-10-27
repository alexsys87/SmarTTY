using SmarTTY.TextEditing;
using System;
using System.Collections;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

public class TokenCache
{
    public class UnpairedTokenSet
    {
        private Dictionary<Token, bool> _tokens = new Dictionary<Token, bool>();

        public int Count => _tokens.Count;

        public void Add(Token token)
        {
            _tokens[token] = true;
        }

        internal void Clear()
        {
            _tokens.Clear();
        }

        public Queue<Token> ToQueue()
        {
            Queue<Token> queue = new Queue<Token>();
            foreach (Token key in _tokens.Keys)
            {
                queue.Enqueue(key);
            }
            return queue;
        }
    }

    public class RegionStyle
    {
        public enum FoldBoundary
        {
            LineStart,
            TokenStart,
            TokenEnd
        }

        public enum LabelSource
        {
            PreviousToken,
            NextToken,
            FixedText,
            RestOfLine
        }

        public readonly FoldBoundary StartBoundary;

        public readonly FoldBoundary EndBoundary;

        public readonly bool UnusedFlag;

        public readonly bool OpeningOnNextLine;

        public readonly LabelSource LabelKind;

        public readonly string Text;

        public readonly int TrailingBlankLines = 1;

        /// <summary>
        /// The closing token marks the line AFTER the block (the next block, a blank line, the end of the document):
        /// the region hides the lines between the opening and the closing token, and that line stays visible.
        /// Blank lines right before the closing token stay visible too. Used by indentation-based and
        /// "ends at the next header" blocks (Python, YAML, UCI, Makefile rules).
        /// </summary>
        public readonly bool EndsBeforeClosingLine;

        public RegionStyle(FoldBoundary startBoundary, FoldBoundary endBoundary, bool unusedFlag, LabelSource labelSource, string text, bool openingOnNextLine, int trailingBlankLines = 1, bool endsBeforeClosingLine = false)
        {
            EndsBeforeClosingLine = endsBeforeClosingLine;
            TrailingBlankLines = trailingBlankLines;
            StartBoundary = startBoundary;
            this.EndBoundary = endBoundary;
            this.UnusedFlag = unusedFlag;
            LabelKind = labelSource;
            Text = text;
            this.OpeningOnNextLine = openingOnNextLine;
        }
    }

    internal class LineTokens : ITokenSink
    {
        public int Line;

        public readonly int StartState;

        private List<Token> _tokens = new List<Token>();

        public List<Token> Tokens => _tokens;

        private int LineIndex => Line;

        int ITokenSink.CurrentLine => LineIndex;

        public bool IsLastLine { get; set; }

        public LineTokens(int lineIndex, int startState)
        {
            this.Line = lineIndex;
            StartState = startState;
        }

        public void AddToken(int type, int start, int end)
        {
            if (start > end)
            {
                throw new ArgumentException();
            }
            _tokens.Add(new Token(this, type, start, end, _tokens.Count));
        }

        public void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, RegionStyle regionStyle)
        {
            // A zero-width token is allowed: it marks a position (the end of a block header line).
            if (start > end)
            {
                throw new ArgumentException();
            }
            _tokens.Add(new Token(this, type, start, end, pairId, isOpening, _tokens.Count, regionStyle));
        }

        public void Detach(UnpairedTokenSet unpairedTokenSet)
        {
            this.Line = -1;
            foreach (Token item in _tokens)
            {
                item.ReleasePartner(unpairedTokenSet);
                item.MarkDeleted();
            }
        }
    }

    public class Token
    {
        public enum TokenKind
        {
            Normal,
            Paired
        }

        internal readonly LineTokens _line;

        private bool _deleted;

        public TokenKind Kind;

        public int Type;

        public const int ConstZero = 0;

        public int PairId = -1;

        public bool IsOpening;

        private Token _partner;

        public readonly int Index;

        public readonly RegionStyle Style;

        public int Start;

        public int End;

        public int VisitMark;

        public bool IsDeleted => _deleted;

        public int LineNumber => _line.Line;

        public TextDocument.TextPosition StartPosition => new TextDocument.TextPosition(Start, LineNumber);

        public TextDocument.TextPosition EndPosition => new TextDocument.TextPosition(End, LineNumber);

        public Token Partner => _partner;

        internal Token(LineTokens lineTokens, int type, int start, int end, int index)
        {
            _line = lineTokens;
            Kind = TokenCache.Token.TokenKind.Normal;
            this.Type = type;
            Start = start;
            End = end;
            Index = index;
        }

        internal Token(LineTokens lineTokens, int type, int start, int end, int pairId, bool isOpening, int index, RegionStyle regionStyle)
            : this(lineTokens, type, start, end, index)
        {
            Kind = TokenCache.Token.TokenKind.Paired;
            PairId = pairId;
            IsOpening = isOpening;
            Style = regionStyle;
        }

        public override string ToString()
        {
            return string.Format("Line {3}, {0} - {1}, length {2}", Start, End, End - Start, LineNumber);
        }

        internal void SetPartner(Token token)
        {
            if (_partner != null)
            {
                _partner._partner = null;
            }
            _partner = token;
            token._partner = this;
        }

        internal void ClearPartner()
        {
            _partner = null;
        }

        internal void ReleasePartner(object sender)
        {
            if (_partner != null)
            {
                if (_partner._line.Line != -1)
                {
                    if (sender is UnpairedTokenSet)
                    {
                        ((UnpairedTokenSet)sender).Add(_partner);
                    }
                    else
                    {
                        if (!(sender is Queue<Token>))
                        {
                            throw new InvalidOperationException();
                        }
                        ((Queue<Token>)sender).Enqueue(_partner);
                    }
                }
                _partner._partner = null;
            }
            _partner = null;
        }

        internal void MarkDeleted()
        {
            _deleted = true;
        }
    }

    public interface ITokenSink
    {
        int CurrentLine { get; }

        /// <summary>The line is the last one of the document (tokenizers close the blocks that are still open).</summary>
        bool IsLastLine { get; }

        void AddToken(int type, int start, int end);

        void AddPairedToken(int type, int start, int end, int pairId, bool isOpening, RegionStyle regionStyle);
    }

    public delegate bool LineTokenizer(int startState, string lineText, ITokenSink tokenSink, out int endState);

    public enum TokenSearchMode
    {
        Previous,
        Next,
        Containing,
        PreviousOrLast
    }

    private TextDocument _document;

    private List<LineTokens> _lines = new List<LineTokens>();

    private UnpairedTokenSet _unpaired = new UnpairedTokenSet();

    private int _visitGeneration;

    public TextDocument Document => _document;

    public int LineCount => _lines.Count;

    public TokenCache(TextDocument textDocument, LineTokenizer callback)
    {
        SetDocument(textDocument, callback);
    }

    public TokenCache()
    {
    }

    private LineTokens TokenizeLine(int lineIndex, LineTokenizer callback, string lineText, ref int startState)
    {
        LineTokens lineTokens = new LineTokens(lineIndex, startState);
        lineTokens.IsLastLine = lineIndex == _document.LineCount - 1;
        if ((callback == null || !callback(startState, lineText, lineTokens, out startState)) && lineText.Length > 0)
        {
            lineTokens.AddToken(startState, 0, lineText.Length);
        }
        return lineTokens;
    }

    public void TokenizeAll(LineTokenizer callback)
    {
        _lines.Clear();
        _unpaired = new UnpairedTokenSet();
        if (_document == null)
        {
            return;
        }
        int currentState = 0;
        int lineIndex = 0;
        foreach (string lineText in _document)
        {
            _lines.Add(TokenizeLine(lineIndex++, callback, lineText, ref currentState));
        }
        PairAll(null);
    }

    private int RetokenizeFrom(int startLineIndex, int endLineIndex, LineTokenizer callback, UnpairedTokenSet unpairedTokenSet, int? startState = null)
    {
        if (startLineIndex >= this._lines.Count)
        {
            return 0;
        }
        int currentState = startState ?? this._lines[startLineIndex].StartState;
        int lineIndex;
        for (lineIndex = startLineIndex; lineIndex < _document.LineCount && (lineIndex <= endLineIndex || currentState != this._lines[lineIndex].StartState); lineIndex++)
        {
            if (this._lines[lineIndex] != null)
            {
                this._lines[lineIndex].Detach(unpairedTokenSet);
            }
            this._lines[lineIndex] = TokenizeLine(lineIndex, callback, _document.GetLineText(lineIndex), ref currentState);
        }
        RepairPairs(startLineIndex, endLineIndex, unpairedTokenSet);
        return lineIndex - startLineIndex;
    }

    public int RetokenizeLine(int lineIndex, LineTokenizer callback)
    {
        return RetokenizeFrom(lineIndex, lineIndex, callback, this._unpaired);
    }

    public void OnLinesChanged(int startLineIndex, int oldLineCount, int newLineCount, LineTokenizer callback)
    {
        // The state in front of the first changed line: lines that are inserted or removed there must not reset it
        // (a new line starts inside the block comment, the string or the indented block that surrounds it).
        int startState = (startLineIndex < this._lines.Count) ? this._lines[startLineIndex].StartState : 0;
        if (oldLineCount > newLineCount)
        {
            for (int i = 0; i < oldLineCount - newLineCount; i++)
            {
                this._lines[startLineIndex + i].Detach(this._unpaired);
            }
            this._lines.RemoveRange(startLineIndex, oldLineCount - newLineCount);
        }
        else if (oldLineCount < newLineCount)
        {
            int addedLines = newLineCount - oldLineCount;
            this._lines.InsertRange(startLineIndex, new LineTokens[addedLines]);
            for (int j = startLineIndex; j < startLineIndex + addedLines; j++)
            {
                this._lines[j] = new LineTokens(j, 0);
            }
        }
        for (int lineIndex = startLineIndex; lineIndex < this._lines.Count; lineIndex++)
        {
            this._lines[lineIndex].Line = lineIndex;
        }
        this.RetokenizeFrom(startLineIndex, startLineIndex + newLineCount, callback, this._unpaired, startState);
    }

    public List<Token> GetLineTokens(int lineIndex)
    {
        return _lines[lineIndex].Tokens;
    }

    public int FindTokenIndex(List<Token> tokens, int offset, TokenSearchMode tokenSearchMode)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start <= offset && tokens[i].End >= offset)
            {
                return i;
            }
        }
        for (int i = 0; i < tokens.Count; i++)
        {
            switch (tokenSearchMode)
            {
                case TokenSearchMode.Previous:
                case TokenSearchMode.PreviousOrLast:
                    if (tokens[i].End <= offset && (i == tokens.Count - 1 || tokens[i + 1].Start >= offset))
                    {
                        return i;
                    }
                    break;
                case TokenSearchMode.Next:
                    if (tokens[i].Start >= offset)
                    {
                        return i;
                    }
                    break;
                case TokenSearchMode.Containing:
                    if (tokens[i].Start < offset && tokens[i].End >= offset)
                    {
                        return i;
                    }
                    break;
            }
        }
        if (tokenSearchMode == TokenSearchMode.PreviousOrLast && tokens.Count != 0)
        {
            return tokens.Count - 1;
        }
        return -1;
    }

    public Token FindToken(int lineIndex, int offset, TokenSearchMode tokenSearchMode)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count)
        {
            return null;
        }
        LineTokens lineTokens = _lines[lineIndex];
        int idx = FindTokenIndex(lineTokens.Tokens, offset, tokenSearchMode);
        if (idx != -1)
        {
            return lineTokens.Tokens[idx];
        }
        return null;
    }

    public Token FindNearestToken(int lineIndex, int offset)
    {
        return FindToken(lineIndex, offset, TokenSearchMode.PreviousOrLast);
    }

    private void PairAll(UnpairedTokenSet unpairedTokenSet)
    {
        PairRange(unpairedTokenSet, 0, _lines.Count - 1);
    }

    private void PairRange(UnpairedTokenSet unpairedTokenSet, int startLineIndex, int endLineIndex)
    {
        Stack<Token> stack = new Stack<Token>();
        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            foreach (Token token in _lines[lineIndex].Tokens)
            {
                token.ClearPartner();
            }
        }
        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            foreach (Token token in _lines[lineIndex].Tokens)
            {
                if (token.Kind != Token.TokenKind.Paired)
                {
                    continue;
                }
                if (token.IsOpening)
                {
                    stack.Push(token);
                    continue;
                }
                if (stack.Count == 0)
                {
                    unpairedTokenSet?.Add(token);
                    continue;
                }
                Token top = stack.Pop();
                if (top.PairId != token.PairId)
                {
                    unpairedTokenSet?.Add(token);
                }
                else
                {
                    token.SetPartner(top);
                }
            }
        }
        if (unpairedTokenSet != null)
        {
            while (stack.Count > 0)
            {
                unpairedTokenSet.Add(stack.Pop());
            }
        }
    }

    private bool FindPartner(Token token, Queue<Token> queue)
    {
        if (token.VisitMark == _visitGeneration)
        {
            return false;
        }
        token.VisitMark = _visitGeneration;
        int step = (token.IsOpening ? 1 : (-1));
        int currentLineIndex = token.LineNumber;
        int currentTokenIndex = token.Index + step;
        LineTokens lineTokens;
        while (true)
        {
            lineTokens = _lines[currentLineIndex];
            if (currentTokenIndex >= lineTokens.Tokens.Count)
            {
                currentTokenIndex = 0;
                if (++currentLineIndex >= _lines.Count)
                {
                    return false;
                }
                continue;
            }
            if (currentTokenIndex < 0)
            {
                if (--currentLineIndex < 0)
                {
                    return false;
                }
                currentTokenIndex = _lines[currentLineIndex].Tokens.Count - 1;
                continue;
            }
            if (lineTokens.Tokens[currentTokenIndex].Kind == Token.TokenKind.Paired)
            {
                if (lineTokens.Tokens[currentTokenIndex].IsOpening == token.IsOpening)
                {
                    if (lineTokens.Tokens[currentTokenIndex].Partner == null && !FindPartner(lineTokens.Tokens[currentTokenIndex], queue))
                    {
                        return false;
                    }
                    currentLineIndex = lineTokens.Tokens[currentTokenIndex].Partner.LineNumber;
                    currentTokenIndex = lineTokens.Tokens[currentTokenIndex].Partner.Index + (lineTokens.Tokens[currentTokenIndex].IsOpening ? 1 : (-1));
                    continue;
                }
                if (lineTokens.Tokens[currentTokenIndex].PairId == token.PairId && lineTokens.Tokens[currentTokenIndex].IsOpening != token.IsOpening)
                {
                    break;
                }
            }
            currentTokenIndex += step;
        }
        lineTokens.Tokens[currentTokenIndex].ReleasePartner(queue);
        token.SetPartner(lineTokens.Tokens[currentTokenIndex]);
        return true;
    }

    private void RepairPairs(int startLineIndex, int endLineIndex, UnpairedTokenSet unpairedTokenSet)
    {
        UnpairedTokenSet localUnpaired = new UnpairedTokenSet();
        PairRange(localUnpaired, startLineIndex, endLineIndex);
        _visitGeneration++;
        Queue<Token> queue = localUnpaired.ToQueue();
        while (queue.Count != 0)
        {
            Token token = queue.Dequeue();
            if (token.Partner == null && token.LineNumber != -1 && !FindPartner(token, queue))
            {
                // Keep going: the remaining tokens in the queue still need a partner.
                unpairedTokenSet.Add(token);
            }
        }
        if (unpairedTokenSet.Count > 0)
        {
            // Tokens that lost their partner in an earlier edit (e.g. a "/*" typed above them and
            // removed again) are not revisited by the incremental repair above. Re-pair the whole
            // document so they can find a partner again; this is a single linear pass.
            unpairedTokenSet.Clear();
            PairRange(unpairedTokenSet, 0, _lines.Count - 1);
        }
    }

    public void SetDocument(TextDocument textDocument, LineTokenizer callback)
    {
        this._unpaired.Clear();
        _document = textDocument;
        TokenizeAll(callback);
        PairAll(null);
    }
}
