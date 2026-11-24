using SmarTTY.TextEditing;
using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The moves of a code editor to a bracket (Go to / Select to the next '(' and the others): the next token the predicate takes, forward or back
/// from the caret, going over the pairs it meets. Moved as it was from <c>CodeEditorPP.MoveToToken</c> (stage 3E); the view selects and moves the
/// caret. Kept as it was: a search back that goes over a pair whose opening bracket is the first token of its line never ends (the index of the
/// token before that bracket is -1, which the next turn of the loop reads as the end of the line, so the same closing bracket is met again).
/// </summary>
public static class TokenMoves
{
    /// <summary>
    /// Where the next matching token is: forward or back (<paramref name="searchPrevious"/>), its start or its end (the start of an opening
    /// token when searching back, of a closing one forward, as <paramref name="isOpening"/> says). False when there is none or the only one is
    /// where the caret is.
    /// </summary>
    public static bool TryFind(TokenCache tokenCache, EditableTextDocument document, TextDocument.TextPosition caret, bool searchPrevious, bool isOpening,
        TokenPredicate callback, out TextDocument.TextPosition target)
    {
        target = caret;
        int step = ((!searchPrevious) ? 1 : (-1));
        int limitLine = (searchPrevious ? (-1) : document.LineCount);
        List<TokenCache.Token> tokens = tokenCache.GetLineTokens(caret.Line);
        int tokenIdx = tokenCache.FindTokenIndex(tokens, caret.Column, (!searchPrevious) ? TokenCache.TokenSearchMode.Next : TokenCache.TokenSearchMode.Previous);
        int currentLine = caret.Line;
        if (tokenIdx == -1)
        {
            currentLine += step;
        }
        for (int line = currentLine; line != limitLine; line += step)
        {
            tokens = tokenCache.GetLineTokens(line);
            int limitToken = (searchPrevious ? (-1) : tokens.Count);
            if (tokenIdx == -1)
            {
                tokenIdx = tokens.Count - 1;
            }
            bool jumped = false;
            for (; tokenIdx != limitToken && tokenIdx != -1 && tokenIdx < tokens.Count; tokenIdx += step)
            {
                TokenCache.Token token = tokens[tokenIdx];
                if (token.Partner == null)
                {
                    continue;
                }
                if ((token.Partner.StartPosition > token.StartPosition) != searchPrevious)
                {
                    tokenIdx = token.Partner.Index + step;
                    line = token.Partner.LineNumber - step;
                    jumped = true;
                    break;
                }
                string text = GetLineOrNull(document, token.LineNumber).Substring(token.Start, token.End - token.Start);
                if (callback(text))
                {
                    TextDocument.TextPosition targetPos = ((searchPrevious == isOpening) ? token.StartPosition : token.EndPosition);
                    if (!(targetPos == caret))
                    {
                        target = targetPos;
                        return true;
                    }
                }
            }
            if (!jumped)
            {
                tokenIdx = (searchPrevious ? (-1) : 0);
            }
        }
        return false;
    }

    // As the view's GetLineOrNull: the text of a line, null for a line past the text.
    private static string GetLineOrNull(EditableTextDocument document, int line)
    {
        if (line >= document.LineCount)
        {
            return null;
        }
        return document.GetLineText(line);
    }
}
