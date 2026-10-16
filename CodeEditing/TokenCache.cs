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

        public Token TakeFirst()
        {
            object obj = _tokens.Keys.GetEnumerator();
            ((IEnumerator)obj).MoveNext();
            Token current = ((IEnumerator<Token>)obj).Current;
            _tokens.Remove(current);
            return current;
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

        public static RegionStyle Ellipsis = new RegionStyle(FoldBoundary.TokenEnd, FoldBoundary.TokenEnd, flag: false, LabelSource.FixedText, "...", flag2: true);

        public static RegionStyle Plain = new RegionStyle(FoldBoundary.LineStart, FoldBoundary.LineStart, flag: false, LabelSource.NextToken, null, flag2: false);

        public RegionStyle(FoldBoundary foldBoundary, FoldBoundary foldBoundary2, bool flag, LabelSource labelSource, string text, bool flag2)
        {
            StartBoundary = foldBoundary;
            this.EndBoundary = foldBoundary2;
            this.UnusedFlag = flag;
            LabelKind = labelSource;
            Text = text;
            this.OpeningOnNextLine = flag2;
        }
    }

    internal class LineTokens : ITokenSink
    {
        public int Line;

        public readonly int StartState;

        private bool _acceptsTokens = true;

        private List<Token> _tokens = new List<Token>();

        public List<Token> Tokens => _tokens;

        private int LineIndex => Line;

        int ITokenSink.CurrentLine => LineIndex;

        public LineTokens(int value2, int value3)
        {
            this.Line = value2;
            StartState = value3;
        }

        public void CloseForAdding()
        {
            _acceptsTokens = false;
        }

        public void AddToken(int value2, int value3, int value4)
        {
            if (_acceptsTokens)
            {
                if (value3 > value4)
                {
                    throw new ArgumentException();
                }
                _tokens.Add(new Token(this, value2, value3, value4, _tokens.Count));
            }
        }

        public void AddPairedToken(int value2, int value3, int value4, int value5, bool flag, RegionStyle regionStyle)
        {
            if (this._acceptsTokens)
            {
                if (value3 >= value4)
                {
                    throw new ArgumentException();
                }
                _tokens.Add(new Token(this, value2, value3, value4, value5, flag, _tokens.Count, regionStyle));
            }
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

        internal Token(LineTokens lineTokens, int value2, int value3, int value4, int value5)
        {
            _line = lineTokens;
            Kind = TokenCache.Token.TokenKind.Normal;
            this.Type = value2;
            Start = value3;
            End = value4;
            Index = value5;
        }

        internal Token(LineTokens lineTokens, int value2, int value3, int value4, int value5, bool flag, int value6, RegionStyle regionStyle)
            : this(lineTokens, value2, value3, value4, value6)
        {
            Kind = TokenCache.Token.TokenKind.Paired;
            PairId = value5;
            IsOpening = flag;
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

        internal void ReleasePartner(object obj)
        {
            if (_partner != null)
            {
                if (_partner._line.Line != -1)
                {
                    if (obj is UnpairedTokenSet)
                    {
                        ((UnpairedTokenSet)obj).Add(_partner);
                    }
                    else
                    {
                        if (!(obj is Queue<Token>))
                        {
                            throw new InvalidOperationException();
                        }
                        ((Queue<Token>)obj).Enqueue(_partner);
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

        void AddToken(int value2, int value3, int value4);

        void AddPairedToken(int value2, int value3, int value4, int value5, bool flag, RegionStyle regionStyle);
    }

    public delegate bool LineTokenizer(int value2, string text, ITokenSink tokenSink, out int value3);

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

    private LineTokens TokenizeLine(int value2, LineTokenizer callback, string text, ref int value3)
    {
        LineTokens lineTokens = new LineTokens(value2, value3);
        if ((callback == null || !callback(value3, text, lineTokens, out value3)) && text.Length > 0)
        {
            lineTokens.AddToken(value3, 0, text.Length);
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
        int num = 0;
        int num2 = 0;
        foreach (string item in _document)
        {
            _lines.Add(TokenizeLine(num2++, callback, item, ref num));
        }
        PairAll(null);
    }

    private int RetokenizeFrom(int value2, int value3, LineTokenizer callback, UnpairedTokenSet unpairedTokenSet)
    {
        if (value2 >= this._lines.Count)
        {
            return 0;
        }
        int num = this._lines[value2].StartState;
        int num2;
        for (num2 = value2; num2 < _document.LineCount && (num2 <= value3 || num != this._lines[num2].StartState); num2++)
        {
            if (this._lines[num2] != null)
            {
                this._lines[num2].Detach(unpairedTokenSet);
            }
            this._lines[num2] = TokenizeLine(num2, callback, _document.GetLineText(num2), ref num);
        }
        RepairPairs(value2, value3, unpairedTokenSet);
        return num2 - value2;
    }

    public int RetokenizeLine(int value2, LineTokenizer callback)
    {
        return RetokenizeFrom(value2, value2, callback, this._unpaired);
    }

    public void OnLinesChanged(int value2, int value3, int value4, LineTokenizer callback)
    {
        if (value3 > value4)
        {
            for (int num = 0; num < value3 - value4; num++)
            {
                this._lines[value2 + num].Detach(this._unpaired);
            }
            this._lines.RemoveRange(value2, value3 - value4);
        }
        else if (value3 < value4)
        {
            int num2 = value4 - value3;
            this._lines.InsertRange(value2, new LineTokens[num2]);
            for (int num3 = value2; num3 < value2 + num2; num3++)
            {
                this._lines[num3] = new LineTokens(num3, 0);
            }
        }
        for (int num4 = value2; num4 < this._lines.Count; num4++)
        {
            this._lines[num4].Line = num4;
        }
        this.RetokenizeFrom(value2, value2 + value4, callback, this._unpaired);
    }

    public List<Token> GetLineTokens(int value2)
    {
        return _lines[value2].Tokens;
    }

    public int FindTokenIndex(List<Token> list, int value2, TokenSearchMode tokenSearchMode)
    {
        for (int num = 0; num < list.Count; num++)
        {
            if (list[num].Start <= value2 && list[num].End >= value2)
            {
                return num;
            }
        }
        for (int num2 = 0; num2 < list.Count; num2++)
        {
            switch (tokenSearchMode)
            {
                case TokenSearchMode.Previous:
                case TokenSearchMode.PreviousOrLast:
                    if (list[num2].End <= value2 && (num2 == list.Count - 1 || list[num2 + 1].Start >= value2))
                    {
                        return num2;
                    }
                    break;
                case TokenSearchMode.Next:
                    if (list[num2].Start >= value2)
                    {
                        return num2;
                    }
                    break;
                case TokenSearchMode.Containing:
                    if (list[num2].Start < value2 && list[num2].End >= value2)
                    {
                        return num2;
                    }
                    break;
            }
        }
        if (tokenSearchMode == TokenSearchMode.PreviousOrLast && list.Count != 0)
        {
            return list.Count - 1;
        }
        return -1;
    }

    public Token FindToken(int value2, int value3, TokenSearchMode tokenSearchMode)
    {
        if (value2 < 0 || value2 >= _lines.Count)
        {
            return null;
        }
        LineTokens lineTokens = _lines[value2];
        int num = FindTokenIndex(lineTokens.Tokens, value3, tokenSearchMode);
        if (num != -1)
        {
            return lineTokens.Tokens[num];
        }
        return null;
    }

    public Token FindNearestToken(int value2, int value3)
    {
        return FindToken(value2, value3, TokenSearchMode.PreviousOrLast);
    }

    private void PairAll(UnpairedTokenSet unpairedTokenSet)
    {
        PairRange(unpairedTokenSet, 0, _lines.Count - 1);
    }

    private void PairRange(UnpairedTokenSet unpairedTokenSet, int value2, int value3)
    {
        Stack<Token> stack = new Stack<Token>();
        for (int num = value2; num <= value3; num++)
        {
            foreach (Token item in _lines[num].Tokens)
            {
                item.ClearPartner();
            }
        }
        for (int num2 = value2; num2 <= value3; num2++)
        {
            foreach (Token item2 in _lines[num2].Tokens)
            {
                if (item2.Kind != Token.TokenKind.Paired)
                {
                    continue;
                }
                if (item2.IsOpening)
                {
                    stack.Push(item2);
                    continue;
                }
                if (stack.Count == 0)
                {
                    unpairedTokenSet?.Add(item2);
                    continue;
                }
                Token token = stack.Pop();
                if (token.PairId != item2.PairId)
                {
                    unpairedTokenSet?.Add(item2);
                }
                else
                {
                    item2.SetPartner(token);
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
        int num = (token.IsOpening ? 1 : (-1));
        int num2 = token.LineNumber;
        int num3 = token.Index + num;
        LineTokens lineTokens;
        while (true)
        {
            lineTokens = _lines[num2];
            if (num3 >= lineTokens.Tokens.Count)
            {
                num3 = 0;
                if (++num2 >= _lines.Count)
                {
                    return false;
                }
                continue;
            }
            if (num3 < 0)
            {
                if (--num2 < 0)
                {
                    return false;
                }
                num3 = _lines[num2].Tokens.Count - 1;
                continue;
            }
            if (lineTokens.Tokens[num3].Kind == Token.TokenKind.Paired)
            {
                if (lineTokens.Tokens[num3].IsOpening == token.IsOpening)
                {
                    if (lineTokens.Tokens[num3].Partner == null && !FindPartner(lineTokens.Tokens[num3], queue))
                    {
                        return false;
                    }
                    num2 = lineTokens.Tokens[num3].Partner.LineNumber;
                    num3 = lineTokens.Tokens[num3].Partner.Index + (lineTokens.Tokens[num3].IsOpening ? 1 : (-1));
                    continue;
                }
                if (lineTokens.Tokens[num3].PairId == token.PairId && lineTokens.Tokens[num3].IsOpening != token.IsOpening)
                {
                    break;
                }
            }
            num3 += num;
        }
        lineTokens.Tokens[num3].ReleasePartner(queue);
        token.SetPartner(lineTokens.Tokens[num3]);
        return true;
    }

    private void RepairPairs(int value2, int value3, UnpairedTokenSet unpairedTokenSet)
    {
        UnpairedTokenSet unpairedTokenSet2 = new UnpairedTokenSet();
        PairRange(unpairedTokenSet2, value2, value3);
        _visitGeneration++;
        Queue<Token> queue = unpairedTokenSet2.ToQueue();
        while (queue.Count != 0)
        {
            Token token = queue.Dequeue();
            if (token.Partner == null && token.LineNumber != -1 && !FindPartner(token, queue))
            {
                unpairedTokenSet.Add(token);
                break;
            }
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
