using SmarTTY.TextEditing;

namespace SmarTTY.CodeEditing;

/// <summary>What the highlight of the bracket at the caret does: nothing new, go away, or show a pair of a kind.</summary>
public enum BracketPairKind
{
    /// <summary>The highlight there is stays (a bracket token without text).</summary>
    Keep,

    /// <summary>No bracket ends at the caret: the highlight goes away.</summary>
    None,

    Paren,

    Bracket,

    Brace,

    Angle,

    /// <summary>A pair of another kind of token.</summary>
    Other,

    /// <summary>A bracket without its pair: only it is highlighted.</summary>
    Unpaired
}

/// <summary>
/// The highlight of the pair of the bracket that ends at the caret: which kind it is and the range from the opening bracket to the end of the
/// closing one (a bracket without a pair: its own range, from its end back to its start). Moved as it was from <c>CodeEditorPP.OnSelectionChanged</c>
/// (stage 3E), so that the WPF text view highlights the same pairs; the views keep the highlight and its style.
/// </summary>
public static class BracketPair
{
    /// <summary>
    /// The pair for the caret at <paramref name="currentCaret"/> (the token is looked for there) and at <paramref name="caretPosition"/> (the bracket
    /// must end there): the two are the same when the view asks. Called only while nothing is selected.
    /// </summary>
    public static BracketPairKind Find(TokenCache tokenCache, TextDocument document, TextDocument.TextPosition currentCaret, TextDocument.TextPosition caretPosition,
        out TextDocument.TextPosition pairStart, out TextDocument.TextPosition pairEnd)
    {
        pairStart = TextDocument.TextPosition.Invalid;
        pairEnd = TextDocument.TextPosition.Invalid;
        TokenCache.Token token = tokenCache.FindNearestToken(currentCaret.Line, currentCaret.Column);
        if (token == null || token.Kind != TokenCache.Token.TokenKind.Paired || caretPosition.Line != token.LineNumber || caretPosition.Column != token.End)
        {
            return BracketPairKind.None;
        }
        string text = document.GetLineText(token.LineNumber);
        string tokenText = "";
        if (token.End <= text.Length)
        {
            tokenText = text.Substring(token.Start, token.End - token.Start);
        }
        if (tokenText.Length < 1)
        {
            return BracketPairKind.Keep;
        }
        BracketPairKind kind;
        switch (tokenText[0])
        {
            case '(':
            case ')':
                kind = BracketPairKind.Paren;
                break;
            case '[':
            case ']':
                kind = BracketPairKind.Bracket;
                break;
            case '{':
            case '}':
                kind = BracketPairKind.Brace;
                break;
            case '<':
            case '>':
                kind = BracketPairKind.Angle;
                break;
            default:
                kind = BracketPairKind.Other;
                break;
        }
        if (token.Partner == null)
        {
            kind = BracketPairKind.Unpaired;
            pairStart = token.StartPosition;
            pairEnd = token.EndPosition;
            if (pairStart < pairEnd)
            {
                pairEnd = token.StartPosition;
                pairStart = token.EndPosition;
            }
        }
        else if (token.StartPosition < token.Partner.StartPosition)
        {
            pairStart = token.StartPosition;
            pairEnd = token.Partner.EndPosition;
        }
        else
        {
            pairStart = token.Partner.StartPosition;
            pairEnd = token.EndPosition;
        }
        return kind;
    }
}
