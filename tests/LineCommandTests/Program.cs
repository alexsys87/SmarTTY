using System;
using System.Collections.Generic;
using System.Threading;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the line commands (CodeEditing/LineCommands.cs), the moves to a bracket (TokenMoves.cs) and the zoom steps (EditorZoom.cs) of a code
// editor: the texts the commands make, where the caret and the selection go, the undo steps they ask for, read-only and no comment style, the
// tokens the moves find and the one search that never ends, and the zoom arithmetic. The view is a fake over a real document that logs what it
// is asked. The answers are those of the code of CodeEditorPP before it moved (the line command run of tests/TextViewTests drives the real editor).
// Run with: dotnet run --project tests/LineCommandTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static TextDocument.TextPosition At(int line, int column) => new TextDocument.TextPosition(column, line);

    private static string P(TextDocument.TextPosition position) => position.Line + ":" + position.Column;

    private sealed class View : ILineCommandHost
    {
        public readonly List<string> Log = new List<string>();

        public EditableTextDocument Text;

        public TextDocument.TextPosition Caret;

        public bool Selected;

        public TextDocument.TextPosition Anchor;

        public bool Editable = true;

        public int Tabs = 4;

        public CommentStyle Comment;

        public int CommentReads;

        public View(params string[] lines)
        {
            Text = new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
        }

        public EditableTextDocument Document => Text;

        public TextDocument.TextPosition CaretPosition => Caret;

        public bool HasSelection => Selected;

        public TextDocument.TextPosition SelectionStart => Anchor;

        public bool CanEditLines => Editable;

        public int TabSize => Tabs;

        public CommentStyle CommentStyle
        {
            get
            {
                CommentReads++;
                return Comment;
            }
        }

        private int Offset(string text, TextDocument.TextPosition position)
        {
            int offset = 0;
            for (int line = 0; line < position.Line; line++)
            {
                offset = text.IndexOf('\n', offset) + 1;
            }
            return offset + position.Column;
        }

        public void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
        {
            Log.Add("replace " + P(start) + " " + P(end) + " '" + text.Replace("\n", "\\n") + "'");
            string all = Text.GetText();
            all = all.Substring(0, Offset(all, start)) + text + all.Substring(Offset(all, end));
            Text = new EditableTextDocument(all.Split('\n'), TextDocument.LineEnding.Lf);
        }

        public void ExecuteAsSingleUndoStep(Action edits)
        {
            Log.Add("step {");
            edits();
            Log.Add("}");
        }

        public void SetSelection(bool select, TextDocument.TextPosition anchor)
        {
            Selected = select;
            Anchor = anchor;
            Log.Add("selection " + (select ? P(anchor) : "-"));
        }

        public void SetCaret(TextDocument.TextPosition position)
        {
            Caret = position;
            Log.Add("caret " + P(position));
        }

        public void RemoveLines(int first, int last)
        {
            Log.Add("remove " + first + "-" + last);
            List<string> lines = new List<string>();
            for (int line = 0; line < Text.LineCount; line++)
            {
                if (line < first || line > last)
                {
                    lines.Add(Text.GetLineText(line));
                }
            }
            Text = new EditableTextDocument(lines.Count == 0 ? new[] { "" } : lines.ToArray(), TextDocument.LineEnding.Lf);
        }

        public void UpdateMaxLineWidth(int line) => Log.Add("width " + line);

        public string All => Text.GetText().Replace("\n", "|");

        public string Take()
        {
            string text = string.Join("; ", Log);
            Log.Clear();
            return text;
        }

        public void Select(TextDocument.TextPosition anchor, TextDocument.TextPosition caret)
        {
            Selected = true;
            Anchor = anchor;
            Caret = caret;
        }
    }

    private static void DuplicateChecks()
    {
        View view = new View("one", "two", "three", "four");
        view.Caret = At(1, 2);
        LineCommands commands = new LineCommands(view);
        commands.DuplicateLines();
        Equal(view.All, "one|two|two|three|four", "the caret line is duplicated");
        Equal(view.Take(), "replace 1:3 1:3 '\\ntwo'; selection -; caret 2:2; width -1", "in front of its line break, one edit (not a step of its own), the caret moved down with the copy");
        view = new View("one", "two", "three", "four");
        view.Select(At(1, 1), At(2, 2));
        commands = new LineCommands(view);
        commands.DuplicateLines();
        Equal(view.All, "one|two|three|two|three|four", "two selected lines");
        Equal(view.Take(), "replace 2:5 2:5 '\\ntwo\\nthree'; selection 3:1; caret 4:2; width -1", "the selection moves down with the copy");
        view = new View("one", "two");
        view.Caret = At(1, 1);
        new LineCommands(view).DuplicateLines();
        Equal(view.All, "one|two|two", "the last line: no final line break is added");
        view = new View("a", "b");
        view.Editable = false;
        new LineCommands(view).DuplicateLines();
        Equal(view.Take(), "", "nothing while read-only");
    }

    private static void MoveChecks()
    {
        View view = new View("one", "two", "three", "four");
        view.Caret = At(1, 1);
        LineCommands commands = new LineCommands(view);
        commands.MoveLines(-1);
        Equal(view.All, "two|one|three|four", "the caret line moves up");
        Equal(view.Take(), "replace 0:0 1:3 'two\\none'; selection -; caret 0:1", "one edit of both lines, the caret goes up with its line, no width");
        commands.MoveLines(-1);
        Equal(view.Take(), "", "the first line does not move up");
        view.Caret = At(3, 0);
        commands.MoveLines(1);
        Equal(view.Take(), "", "the last line does not move down");
        commands.MoveLines(1);
        view.Caret = At(2, 2);
        commands.MoveLines(1);
        Equal(view.All, "two|one|four|three", "a line moves down");
        Equal(view.Take(), "replace 2:0 3:4 'four\\nthree'; selection -; caret 3:2", "the caret goes down with it");
        view = new View("one", "two", "three", "four");
        view.Select(At(1, 0), At(3, 0));
        new LineCommands(view).MoveLines(1);
        Equal(view.All, "one|four|two|three", "a selection that ends at the start of a line does not take that line");
        view = new View("a", "b");
        view.Caret = At(1, 0);
        view.Editable = false;
        new LineCommands(view).MoveLines(-1);
        Equal(view.Take(), "", "nothing while read-only");
    }

    private static void DeleteChecks()
    {
        View view = new View("one", "two", "three");
        view.Select(At(0, 1), At(1, 1));
        new LineCommands(view).DeleteLines();
        Equal(view.All, "three", "the selected lines go");
        Equal(view.Take(), "remove 0-1; selection -; width -1", "removed by the view, the selection cleared at the caret");
        view = new View("one", "two");
        view.Caret = At(1, 1);
        new LineCommands(view).DeleteLines();
        Equal(view.Take(), "remove 1-1; selection -; width -1", "the caret line");
        view.Editable = false;
        new LineCommands(view).DeleteLines();
        Equal(view.Take(), "", "nothing while read-only");
    }

    private static void CommentChecks()
    {
        View view = new View("a", "  b", "", "c");
        LineCommands commands = new LineCommands(view);
        Equal(commands.ToggleComment(), false, "no comment style: nothing");
        Equal(view.Take(), "", "and no edit");
        view.Comment = new CommentStyle("// ");
        view.CommentReads = 0;
        view.Select(At(0, 0), At(3, 1));
        Equal(commands.ToggleComment(), true, "with a comment style");
        Equal(view.All, "// a|//   b||// c", "the lines are commented, the empty one is left");
        Equal(view.CommentReads, 2, "the style is read for the test and for the toggle, as before");
        Equal(view.Take(), "step {; replace 0:0 0:0 '// '; replace 1:0 1:0 '// '; replace 3:0 3:0 '// '; }; selection 0:0; caret 3:4; width -1",
            "one undo step of the smallest edits, the caret moved with its text");
        commands.ToggleComment();
        Equal(view.All, "a|  b||c", "uncommented");
        Equal(P(view.Caret), "3:1", "the caret back");
        view.Selected = false;
        view.Caret = At(1, 3);
        commands.ToggleComment();
        Equal(view.All, "a|  // b||c", "the caret line, after its indentation");
        Equal(P(view.Caret), "1:6", "the caret after the mark");
        view.Comment = new CommentStyle("<!-- ", " -->");
        view.Caret = At(3, 0);
        commands.ToggleComment();
        Equal(view.All, "a|  // b||<!-- c -->", "a comment with an end");
        Equal(P(view.Caret), "3:0", "a caret at the start of the line stays");
        view.Editable = false;
        view.Take();
        Equal(commands.ToggleComment(), false, "read-only: nothing");
        Equal(view.Take(), "", "and no edit");
    }

    private static void TransformChecks()
    {
        View view = new View("b ", "a", "b ", "\tc  ", "a");
        view.Caret = At(3, 2);
        LineCommands commands = new LineCommands(view);
        Equal(commands.ApplyLineTransform(LineTransformKind.TrimTrailingWhitespace), true, "trim without a selection");
        Equal(view.All, "b|a|b|\tc|a", "every line trimmed");
        Equal(view.Take(), "step {; replace 0:1 0:2 ''; replace 2:1 2:2 ''; replace 3:2 3:4 ''; }; selection -; caret 3:2; width -1", "the smallest edits in one step");
        Equal(commands.ApplyLineTransform(LineTransformKind.SortAscending), false, "sorting needs two selected lines");
        Equal(commands.ApplyLineTransform(LineTransformKind.RemoveDuplicateLines), false, "and so does removing duplicates");
        Equal(commands.ApplyLineTransform(LineTransformKind.IndentationToSpaces), true, "tabs to spaces");
        Equal(view.All, "b|a|b|    c|a", "the tab is four blanks");
        Equal(P(view.Caret), "3:5", "the caret moved with its text");
        Equal(commands.ApplyLineTransform(LineTransformKind.IndentationToSpaces), true, "again: true");
        Equal(view.Take(), "step {; replace 3:0 3:1 '    '; }; selection -; caret 3:5; width -1", "the first time an edit, the second time nothing at all");
        Equal(commands.ApplyLineTransform(LineTransformKind.EnsureFinalNewline), true, "a final line break");
        Equal(view.All, "b|a|b|    c|a|", "added");
        Equal(view.Take(), "step {; replace 4:1 4:1 '\\n'; }; width -1", "in one step, the caret and the selection left alone");
        Equal(commands.ApplyLineTransform(LineTransformKind.EnsureFinalNewline), true, "already there: true");
        Equal(view.Take(), "", "and nothing done");
        view = new View("c", "b", "a", "b");
        view.Select(At(1, 0), At(3, 1));
        commands = new LineCommands(view);
        Equal(commands.HasLineSelection, true, "two lines or more are selected");
        Equal(commands.ApplyLineTransform(LineTransformKind.SortAscending), true, "sort the selected lines");
        Equal(view.All, "c|a|b|b", "sorted");
        Equal(view.Take(), "step {; replace 1:0 1:1 'a'; replace 2:0 2:1 'b'; }; selection 1:0; caret 3:1; width -1", "the lines that changed");
        Equal(commands.ApplyLineTransform(LineTransformKind.RemoveDuplicateLines), true, "duplicates of the selection");
        Equal(view.All, "c|a|b", "removed");
        Equal(view.Take(), "step {; replace 1:0 3:1 'a\\nb'; }; selection 1:0; caret 2:1; width -1", "the block replaced and selected whole");
        view.Selected = false;
        Equal(commands.HasLineSelection, false, "no selection: no line selection");
        view = new View("x ");
        view.Editable = false;
        Equal(new LineCommands(view).ApplyLineTransform(LineTransformKind.TrimTrailingWhitespace), false, "read-only: false");
        Equal(new LineCommands(view).ApplyLineTransform(LineTransformKind.EnsureFinalNewline), false, "even for the final line break");
        Equal(view.Take(), "", "and nothing done");
        view = new View("");
        Equal(new LineCommands(view).ApplyLineTransform(LineTransformKind.EnsureFinalNewline), true, "an empty text needs no final line break");
        Equal(view.Take(), "", "nothing done");
    }

    private static TokenCache Tokens(EditableTextDocument document)
    {
        TokenCache cache = new TokenCache();
        cache.SetDocument(document, SyntaxHighlighting.GetTokenizer(SyntaxHighlighting.Language.CLike));
        return cache;
    }

    private static string Find(TokenCache cache, EditableTextDocument document, TextDocument.TextPosition caret, bool back, bool opening, string token)
    {
        bool found = TokenMoves.TryFind(cache, document, caret, back, opening, text => text == token, out TextDocument.TextPosition target);
        return found ? P(target) : "-" + P(target);
    }

    private static void TokenChecks()
    {
        EditableTextDocument document = new EditableTextDocument(new[] { "f(a, g(b), [c]) {", "  x = <y>;", "}" }, TextDocument.LineEnding.Lf);
        TokenCache cache = Tokens(document);
        Equal(Find(cache, document, At(0, 3), false, true, ")"), "0:15", "forward: the closing bracket of the call, past the pairs inside it, its end");
        Equal(Find(cache, document, At(0, 3), false, false, ")"), "0:14", "its start for a closing token asked for as closing");
        Equal(Find(cache, document, At(0, 3), false, true, "("), "-0:3", "forward finds no opening bracket: it goes over its pair");
        Equal(Find(cache, document, At(0, 2), false, true, ")"), "-0:2", "from just after an opening bracket: the pair is gone over");
        Equal(Find(cache, document, At(0, 10), true, true, "("), "0:1", "back: the opening bracket of the call, its start");
        Equal(Find(cache, document, At(0, 10), true, false, "("), "0:2", "its end");
        Equal(Find(cache, document, At(0, 7), true, false, "("), "0:2", "a token that ends at the caret is not a move: the one before it");
        Equal(Find(cache, document, At(0, 10), true, true, ")"), "-0:10", "back finds no closing bracket");
        Equal(Find(cache, document, At(0, 3), false, true, "}"), "-0:3", "forward over a pair to the next line");
        EditableTextDocument lines = new EditableTextDocument(new[] { "{ (", "x", ") }" }, TextDocument.LineEnding.Lf);
        TokenCache lineCache = Tokens(lines);
        // A caret with no token after it in its line: the search goes on in the next line, but from its last token (the index -1 of
        // "none" is read as the end of the line in both directions), so forward a token before it in that line is not met.
        Equal(Find(lineCache, lines, At(1, 0), false, true, ")"), "-1:0", "forward from a line without tokens: the bracket first in the next line is not met");
        Equal(Find(lineCache, lines, At(1, 0), false, true, "}"), "2:3", "the last token of the next line is");
        Equal(Find(lineCache, lines, At(1, 0), true, true, "("), "0:2", "back to the line before, from its last token");
        // A line left without a match: the next line is searched from its first token forward (and the closing bracket after the pair there is
        // not met, the pair is gone over as a whole).
        EditableTextDocument open = new EditableTextDocument(new[] { "{([", "  ((a)" }, TextDocument.LineEnding.Lf);
        Equal(Find(Tokens(open), open, At(0, 0), false, true, ")"), "-0:0", "forward over unclosed brackets and on from the first token of the next line");
        EditableTextDocument plain = new EditableTextDocument(new[] { "abc", "def" }, TextDocument.LineEnding.Lf);
        Equal(Find(Tokens(plain), plain, At(0, 1), false, true, ")"), "-0:1", "no brackets: nothing forward");
        Equal(Find(Tokens(plain), plain, At(1, 1), true, true, "("), "-1:1", "nor back");

        // Kept as it was: back over a pair whose opening bracket is the first token of its line, the search never ends.
        EditableTextDocument loop = new EditableTextDocument(new[] { "a(b)" }, TextDocument.LineEnding.Lf);
        TokenCache loopCache = Tokens(loop);
        bool ended = false;
        Thread thread = new Thread(() =>
        {
            TokenMoves.TryFind(loopCache, loop, At(0, 4), true, true, text => text == "(", out _);
            ended = true;
        }) { IsBackground = true };
        thread.Start();
        Equal(thread.Join(2000) || ended, false, "the search back from after a(b) does not end (the fault, kept as it was)");
    }

    private static void ZoomChecks()
    {
        Equal(EditorZoom.By(1f, 1), 1.1f, "a step in");
        Equal(EditorZoom.By(1f, -2), 0.8f, "two steps out");
        Equal(EditorZoom.By(1.23f, 1), 13 * 0.1f, "from between the tenths: to the next tenth (13 steps of a float tenth)");
        Equal(EditorZoom.By(1.27f, -1), 12 * 0.1f, "and back: the nearest tenth less one");
        Equal(EditorZoom.Round(1.234f), 1.23f, "a zoom set is rounded to hundredths");
        Equal(EditorZoom.Round(1.235f), (float)Math.Round(1.235f, 2), "as Math.Round does it");
        Equal(EditorZoom.Clamp(10f), 4f, "the largest");
        Equal(EditorZoom.Clamp(0.1f), 0.5f, "the smallest");
        Equal(EditorZoom.Clamp(1.5f), 1.5f, "between them");
        Equal(EditorZoom.Min, 0.5f, "the smallest is half");
        Equal(EditorZoom.Max, 4f, "the largest four times");
    }

    private static void Main()
    {
        DuplicateChecks();
        MoveChecks();
        DeleteChecks();
        CommentChecks();
        TransformChecks();
        TokenChecks();
        ZoomChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line command checks passed.");
    }
}
