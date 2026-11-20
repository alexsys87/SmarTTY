using SmarTTY.TextEditing;
using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The text view as <see cref="TextReplace"/> sees it (made of the calls <c>ReplaceTextCore</c> and <c>ReplaceAll</c> of
/// <c>Controls/EditableTextBrowser</c> made on it): what its edit commands see, and whether it shows one line, the line ending of a text put
/// in, the text and the indentation of a line, and its own entry for an edit.
/// </summary>
public interface ITextReplaceHost : ITextEditView
{
    /// <summary>The view shows one line: the line breaks of a text put in are dropped.</summary>
    bool SingleLine { get; }

    /// <summary>A text with line breaks takes the line ending of the line it goes into.</summary>
    bool UseExistingLineEnding { get; }

    /// <summary>The text of a line; null past the end of the text.</summary>
    string GetLineOrNull(int line);

    /// <summary>The blanks at the start of a line.</summary>
    string GetIndentation(int line);

    /// <summary>The view's entry for an edit (the view may route it, as the terminals do; nothing happens in a read-only text).</summary>
    void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text);
}

/// <summary>
/// What an edit of the text view becomes: a replacement within a line or of a range, or nothing (an empty edit); the columns -1 that stand for the
/// end of a line, a single-line view, the indentation that the join of two lines drops; and Replace All, as one undo step, in the whole text or in
/// a range, with the end of the range afterwards. It is what <c>Controls/EditableTextBrowser</c> did in <c>ReplaceTextCore</c>, <c>ReplaceAll</c>
/// and <c>CountLineBreaks</c>, moved here as it was (stage 3D) so that a text view of another toolkit edits with the same code; the view keeps
/// read-only and its events.
/// </summary>
public sealed class TextReplace
{
    private readonly ITextReplaceHost _host;

    private readonly UndoHistory _history;

    public TextReplace(ITextReplaceHost host, UndoHistory history)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    private EditableTextDocument Doc => _host.Document;

    /// <summary>
    /// Replaces the text between two positions (in either order) by <paramref name="replacement"/>; with <paramref name="select"/> the text is
    /// put at <paramref name="start"/> and nothing is removed. A column of -1 is the end of its line.
    /// </summary>
    public void Replace(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string replacement)
    {
        if (select)
        {
            end = start;
        }
        if (_host.SingleLine)
        {
            replacement = replacement.Replace("\r\n", "").Replace("\n", "");
        }
        if (start.Column == -1)
        {
            start.Column = _host.GetLineOrNull(start.Line).Length;
        }
        if (end.Column == -1)
        {
            end.Column = _host.GetLineOrNull(end.Line).Length;
        }
        if (start == end && string.IsNullOrEmpty(replacement))
        {
            // Nothing to change (e.g. Backspace at the start of the document): do not mark the
            // file as modified and do not record an empty undo step.
            return;
        }
        if (replacement == "" && end.Column == 0 && end.Line == start.Line + 1 && start.Column == Doc.GetLineText(start.Line).Length)
        {
            end.Column = IndentEdits.SharedIndentLength(_host.GetIndentation(start.Line), Doc.GetLineText(end.Line));
        }
        if (start.Line == end.Line && replacement.IndexOfAny(new char[2] { '\r', '\n' }) == -1)
        {
            _history.Execute(new ReplaceInLineCommand(_host, start.Line, Math.Min(start.Column, end.Column), Math.Abs(start.Column - end.Column), replacement));
            return;
        }
        TextDocument.TextPosition rangeStart;
        TextDocument.TextPosition rangeEnd;
        if (select)
        {
            rangeStart = (rangeEnd = start);
        }
        else if (start < end)
        {
            rangeStart = start;
            rangeEnd = end;
        }
        else
        {
            rangeStart = end;
            rangeEnd = start;
        }
        _history.Execute(new ReplaceRangeCommand(_host, rangeStart, rangeEnd, replacement, _host.UseExistingLineEnding));
    }

    /// <summary>
    /// Replaces every match of <paramref name="matcher"/> (a regular expression replacement may use $1, ${name}) as one undo step and returns how
    /// many were replaced; the view has checked that the text can be edited and that there is a matcher. A valid <paramref name="scopeStart"/> and
    /// <paramref name="scopeEnd"/> limit it to that range, and <paramref name="newScopeEnd"/> is where the end of the range is afterwards. The caret
    /// stays where it was (brought into its line).
    /// </summary>
    public int ReplaceAll(TextMatcher matcher, string replacement, TextDocument.TextPosition scopeStart, TextDocument.TextPosition scopeEnd, out TextDocument.TextPosition newScopeEnd)
    {
        newScopeEnd = scopeEnd;
        replacement = replacement ?? "";
        bool scoped = scopeStart.IsValid && scopeEnd.IsValid;
        TextDocument.TextPosition caret = _host.CaretPosition;
        int count = 0;
        int firstLine = scoped ? Math.Max(0, scopeStart.Line) : 0;
        int lastLine = scoped ? scopeEnd.Line : (Doc.LineCount - 1);
        int endLine = scopeEnd.Line;
        int endColumn = scopeEnd.Column;
        _history.ExecuteAsSingleStep(() =>
        {
            for (int line = firstLine; line <= lastLine && line < Doc.LineCount; line++)
            {
                string text = Doc.GetLineText(line);
                int from = (scoped && line == scopeStart.Line) ? scopeStart.Column : 0;
                int to = (scoped && line == scopeEnd.Line) ? Math.Min(scopeEnd.Column, text.Length) : text.Length;
                string replaced = matcher.ReplaceAll(text, from, to, replacement, out int lineCount, out int newTo);
                if (lineCount == 0)
                {
                    continue;
                }
                count += lineCount;
                _host.ReplaceText(new TextDocument.TextPosition(0, line), new TextDocument.TextPosition(text.Length, line), select: false, replaced);
                // A replacement with line breaks adds lines: do not search the inserted text again.
                int added = CountLineBreaks(replaced);
                if (scoped && line == scopeEnd.Line)
                {
                    int lastBreak = replaced.LastIndexOfAny(new[] { '\r', '\n' });
                    endLine = scopeEnd.Line + added;
                    endColumn = (added == 0) ? newTo : Math.Max(0, newTo - (lastBreak + 1));
                }
                line += added;
                lastLine += added;
            }
        });
        if (scoped)
        {
            newScopeEnd = new TextDocument.TextPosition(endColumn, endLine);
        }
        if (count > 0)
        {
            int caretLine = Math.Min(caret.Line, Math.Max(0, Doc.LineCount - 1));
            _host.SetSelection(false, _host.CaretPosition);
            _host.SetCaret(new TextDocument.TextPosition(Math.Min(caret.Column, Doc.GetLineText(caretLine).Length), caretLine));
            _host.Redraw();
        }
        return count;
    }

    /// <summary>The line breaks of a text: LF, CR LF and a lone CR.</summary>
    public static int CountLineBreaks(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                count++;
            }
        }
        return count;
    }
}
