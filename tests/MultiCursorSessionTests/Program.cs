using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of what a code editor does with several cursors (CodeEditing/MultiCursorSession.cs): the set the cursors make with the caret and the
// selection, the commands that make them, typing and the keys with them, the edits of a plan as one undo step, copy, cut and paste, Alt+click, and
// going back to one cursor. The view is a fake over a real document that logs what it is asked; the operations on the set are the real
// MultiCursor. The answers are those of CodeEditorPP before it moved (the cursors run of tests/TextViewTests drives the real editor).
// Run with: dotnet run --project tests/MultiCursorSessionTests
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

    private sealed class View : IMultiCursorHost
    {
        public readonly List<string> Log = new List<string>();

        public EditableTextDocument Text;

        public TextDocument.TextPosition Caret;

        public bool Selected;

        public TextDocument.TextPosition Anchor;

        public bool Editable = true;

        public bool Spaces;

        public int Tabs = 4;

        public MultiCursorSession Session;

        public View(params string[] lines)
        {
            Text = new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
            Session = new MultiCursorSession(this);
        }

        public EditableTextDocument Document => Text;

        public TextDocument.TextPosition CaretPosition => Caret;

        public bool HasSelection => Selected;

        public TextDocument.TextPosition SelectionStart => Anchor;

        public bool CanUseCursors => Editable;

        public bool InsertSpaces => Spaces;

        public int TabSize => Tabs;

        // A change of the view that is not the cursors' own takes them away, as the editor's hooks do.
        public void SetSelection(bool select, TextDocument.TextPosition anchor)
        {
            Selected = select;
            Anchor = anchor;
            Session.OnViewChanged();
        }

        public void SetCaret(TextDocument.TextPosition position)
        {
            Caret = position;
            Session.OnViewChanged();
        }

        private static int Offset(string text, TextDocument.TextPosition position)
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
            Log.Add("replace " + P(start) + "-" + P(end) + " '" + text.Replace("\n", "\\n") + "'" + (Session.IsApplying ? "" : " (not the cursors')"));
            string all = Text.GetText();
            all = all.Substring(0, Offset(all, start)) + text + all.Substring(Offset(all, end));
            Text = new EditableTextDocument(all.Split('\n'), TextDocument.LineEnding.Lf);
            Session.OnViewChanged();
        }

        public void ExecuteAsSingleUndoStep(Action edits)
        {
            Log.Add("step {");
            edits();
            Log.Add("}");
        }

        public void UpdateMaxLineWidth(int line) => Log.Add("width " + line);

        public void HideCompletion() => Log.Add("hide completion");

        public void Redraw() => Log.Add("redraw");

        public string All => Text.GetText().Replace("\n", "|");

        public string Cursors
        {
            get
            {
                List<string> items = new List<string>();
                foreach (TextCursor cursor in Session.ExtraCursors)
                {
                    items.Add(cursor.Anchor.Line + ":" + cursor.Anchor.Column + (cursor.HasSelection ? ">" + cursor.Caret.Line + ":" + cursor.Caret.Column : ""));
                }
                return (Selected ? P(Anchor) + ">" : "") + P(Caret) + " + " + (items.Count == 0 ? "-" : string.Join(" ", items));
            }
        }

        public string Take()
        {
            string text = string.Join("; ", Log);
            Log.Clear();
            return text;
        }
    }

    private static View ThreeLines(params string[] lines)
    {
        View view = new View(lines);
        view.Caret = At(0, 0);
        view.Session.AddCursorBelow();
        view.Session.AddCursorBelow();
        view.Take();
        return view;
    }

    private static void CommandChecks()
    {
        View view = new View("foo bar foo", "baz foo", "foo");
        view.Caret = At(0, 1);
        Equal(view.Session.HasMultipleCursors, false, "one cursor at first");
        Equal(view.Session.CursorCount, 1, "a count of one");
        view.Session.AddNextOccurrence();
        Equal(view.Cursors, "0:0>0:3 + -", "Ctrl+D selects the word under the caret");
        Equal(view.Take(), "hide completion; redraw", "it closes the word list and draws again");
        view.Session.AddNextOccurrence();
        Equal(view.Cursors, "0:8>0:11 + 0:0>0:3", "again: the next occurrence, the main cursor on it");
        Equal(view.Session.CursorCount, 2, "two cursors");
        view.Session.AddNextOccurrence();
        view.Session.AddNextOccurrence();
        Equal(view.Cursors, "2:0>2:3 + 0:0>0:3 0:8>0:11 1:4>1:7", "and every other");
        view.Session.ClearExtraCursors();
        Equal(view.Cursors, "2:0>2:3 + -", "back to one: the main cursor stays");
        Equal(view.Take(), "hide completion; redraw; hide completion; redraw; hide completion; redraw; redraw", "each command drawn again, and the clear");
        view.Session.ClearExtraCursors();
        Equal(view.Take(), "", "with one cursor nothing to clear");
        view = new View("foo foobar foo");
        view.Caret = At(0, 1);
        view.Session.AddNextOccurrence();
        view.Session.AddNextOccurrence();
        Equal(view.Cursors, "0:11>0:14 + 0:0>0:3", "a word taken from under the caret: its next occurrence as a whole word");
        view = new View("foo bar foo", "baz foo", "foo");
        view.Caret = At(1, 5);
        view.Session.SelectAllOccurrences();
        Equal(view.Cursors, "1:4>1:7 + 0:0>0:3 0:8>0:11 2:0>2:3", "Ctrl+Shift+L: every occurrence of the word");
        view = new View("a", "b");
        view.Caret = At(1, 0);
        view.Session.AddCursorBelow();
        Equal(view.Take(), "", "no line below: nothing, not even the word list closed");
        view.Session.AddCursorAbove();
        Equal(view.Cursors, "0:0 + 1:0", "a cursor on the line above");
        view = new View("a", "b");
        view.Editable = false;
        view.Session.AddCursorBelow();
        Equal(view.Cursors, "0:0 + -", "read-only: no cursors");
        Equal(MultiCursorSession.IsMultiCursorCommand("AddNextOccurrence") && MultiCursorSession.IsMultiCursorCommand("SelectAllOccurrences")
            && MultiCursorSession.IsMultiCursorCommand("AddCursorAbove") && MultiCursorSession.IsMultiCursorCommand("AddCursorBelow"), true, "the commands of several cursors");
        Equal(MultiCursorSession.IsMultiCursorCommand("DuplicateLines") || MultiCursorSession.IsMultiCursorCommand("ZoomIn"), false, "the others are for one");
    }

    private static void TypingChecks()
    {
        View view = ThreeLines("a", "bb", "ccc");
        Equal(view.Cursors, "2:0 + 0:0 1:0", "three cursors");
        Equal(view.Session.TypeCharacter('-', false), true, "a letter is the cursors'");
        Equal(view.All, "-a|-bb|-ccc", "typed at each");
        Equal(view.Take(), "hide completion; step {; replace 2:0-2:0 '-'; replace 1:0-1:0 '-'; replace 0:0-0:0 '-'; }; redraw; width -1",
            "one undo step, the last edit first, the cursors drawn again, the widths measured");
        Equal(view.Cursors, "2:1 + 0:1 1:1", "the cursors after it");
        view.Session.TypeCharacter('\r', false);
        Equal(view.All, "-|a|-|bb|-|ccc", "Enter at each");
        view.Session.TypeCharacter('\b', false);
        Equal(view.All, "-a|-bb|-ccc", "Backspace at each");
        view.Spaces = true;
        view.Session.TypeCharacter('\t', false);
        Equal(view.All, "-   a|-   bb|-   ccc", "Tab with spaces to the next stop");
        view.Session.TypeCharacter('\b', false);
        Equal(view.All, "-  a|-  bb|-  ccc", "Backspace at a stop after fewer spaces than a level takes one");
        view.Spaces = false;
        view.Session.TypeCharacter('\t', false);
        Equal(view.All, "-  \ta|-  \tbb|-  \tccc", "Tab with a tab");
        view.Take();
        Equal(view.Session.TypeCharacter('\u0001', false), true, "a control character is dealt with");
        Equal(view.Take(), "", "and types nothing, the word list stays");
        Equal(view.Session.TypeCharacter('\t', true), false, "Shift+Tab is for one cursor");
        Equal(view.Cursors, "2:4 + -", "the others are gone");
        Equal(view.Session.TypeCharacter('x', false), false, "with one cursor the view types");
        view = ThreeLines("a", "b", "c");
        view.Spaces = true;
        view.Session.TypeCharacter('\t', false);
        view.Session.TypeCharacter('\b', false);
        Equal(view.All, "a|b|c", "Backspace after a tab of spaces takes the level");
        view = ThreeLines("a", "b", "c");
        view.Editable = false;
        Equal(view.Session.TypeCharacter('x', false), false, "read-only: not the cursors'");
    }

    private static void KeyChecks()
    {
        View view = ThreeLines("one two", "three four", "five");
        Equal(view.Session.KeyDown(VirtualKey.Right, false, true, false), CursorKeyResult.Handled, "Shift+Right");
        Equal(view.Cursors, "2:0>2:1 + 0:0>0:1 1:0>1:1", "selects a letter at each");
        Equal(view.Take(), "hide completion; redraw", "the word list closed");
        view.Session.KeyDown(VirtualKey.Right, true, false, false);
        Equal(view.Cursors, "2:4 + 0:3 1:5", "Ctrl+Right: word right");
        view.Session.KeyDown(VirtualKey.Left, true, false, false);
        Equal(view.Cursors, "2:0 + 0:0 1:0", "Ctrl+Left: word left");
        view.Session.KeyDown(VirtualKey.End, false, false, false);
        Equal(view.Cursors, "2:4 + 0:7 1:10", "End");
        view.Session.KeyDown(VirtualKey.Home, false, false, false);
        Equal(view.Cursors, "2:0 + 0:0 1:0", "Home");
        view.Session.KeyDown(VirtualKey.Up, false, false, false);
        Equal(view.Cursors, "1:0 + 0:0", "Up: the cursors that meet are one");
        Equal(view.Session.KeyDown(VirtualKey.Left, false, false, true), CursorKeyResult.NotHandled, "Alt+Left is not theirs");
        Equal(view.Session.KeyDown(VirtualKey.Home, false, true, true), CursorKeyResult.NotHandled, "nor Alt+Shift+Home");
        Equal(view.Session.KeyDown(VirtualKey.Up, true, false, false), CursorKeyResult.NotHandled, "nor Ctrl+Up");
        Equal(view.Session.KeyDown(VirtualKey.Q, false, false, false), CursorKeyResult.NotHandled, "nor a letter");
        Equal(view.Cursors, "1:0 + 0:0", "the cursors stay");
        view.Session.KeyDown(VirtualKey.Down, false, false, false);
        view.Take();
        Equal(view.Session.KeyDown(VirtualKey.Escape, false, false, false), CursorKeyResult.Handled, "Escape");
        Equal(view.Cursors, "2:0 + -", "goes back to one cursor");
        Equal(view.Take(), "redraw; hide completion", "and closes the word list");
        foreach (VirtualKey key in new[] { VirtualKey.PageUp, VirtualKey.PageDown })
        {
            view = ThreeLines("a", "b", "c");
            Equal(view.Session.KeyDown(key, false, false, false), CursorKeyResult.NotHandled, key + ": one cursor, then the view's");
            Equal(view.Session.HasMultipleCursors, false, key + " cleared them");
        }
        foreach (VirtualKey key in new[] { VirtualKey.A, VirtualKey.Z, VirtualKey.Y, VirtualKey.Home, VirtualKey.End })
        {
            view = ThreeLines("a", "b", "c");
            Equal(view.Session.KeyDown(key, true, false, false), CursorKeyResult.NotHandled, "Ctrl+" + key + ": one cursor, then the view's");
            Equal(view.Session.HasMultipleCursors, false, "Ctrl+" + key + " cleared them");
        }
        view = ThreeLines("a", "b", "c");
        view.Session.KeyDown(VirtualKey.A, true, false, true);
        Equal(view.Session.HasMultipleCursors, true, "Ctrl+Alt+A (AltGr) leaves them");

        view = ThreeLines("foo bar", "foo bar", "foo bar");
        Equal(view.Session.KeyDown(VirtualKey.Delete, true, false, false), CursorKeyResult.Handled, "Ctrl+Delete");
        Equal(view.All, " bar| bar| bar", "takes a word at each");
        view.Session.KeyDown(VirtualKey.Delete, false, false, false);
        Equal(view.All, "bar|bar|bar", "Delete a letter at each");
        view.Session.KeyDown(VirtualKey.End, false, false, false);
        Equal(view.Session.KeyDown(VirtualKey.Back, true, false, false), CursorKeyResult.HandledSuppressKeyPress, "Ctrl+Backspace, its character kept out");
        Equal(view.All, "||", "takes the word before each");
        Equal(view.Session.KeyDown(VirtualKey.Back, true, true, false), CursorKeyResult.NotHandled, "Ctrl+Shift+Backspace is not theirs");
        Equal(view.Session.KeyDown(VirtualKey.Back, false, false, false), CursorKeyResult.NotHandled, "nor Backspace (it is a character)");
        Equal(view.Session.KeyDown(VirtualKey.Delete, false, true, false), CursorKeyResult.NotHandled, "nor Shift+Delete");
        Equal(view.Session.KeyDown(VirtualKey.Delete, false, false, true), CursorKeyResult.NotHandled, "nor Alt+Delete");
        view = new View("a");
        Equal(view.Session.KeyDown(VirtualKey.Escape, false, false, false), CursorKeyResult.NotHandled, "with one cursor no key is theirs");
    }

    private static void ClipboardChecks()
    {
        View view = ThreeLines("one two", "three four", "five");
        view.Session.KeyDown(VirtualKey.Right, true, true, false);
        Equal(view.Session.TryCopyCursors(out List<string> texts), true, "copy with several cursors");
        Equal(string.Join("|", texts), "one|three|five", "their texts in order");
        Equal(MultiCursorSession.ForClipboard(texts), "one\r\nthree\r\nfive", "one under the other with CR LF");
        Equal(MultiCursorSession.ForClipboard(new[] { "a\nb", "c\r\nd" }), "a\r\nb\r\nc\r\nd", "the line breaks inside a text become CR LF");
        view.Take();
        Equal(view.Session.TryPaste("A\r\nB\r\nC"), true, "a paste of a line for each cursor");
        Equal(view.All, "A two|B four|C", "each its line");
        Equal(view.Take(), "hide completion; step {; replace 2:0-2:4 'C'; replace 1:0-1:5 'B'; replace 0:0-0:3 'A'; }; redraw; width -1", "in one step");
        view.Session.TryPaste("Z");
        Equal(view.All, "AZ two|BZ four|CZ", "one text for all");
        Equal(view.Session.TryPaste(""), false, "an empty paste is the view's");
        string copied = null;
        view.Session.KeyDown(VirtualKey.Left, true, true, false);
        Equal(view.Session.TryCut(text => { copied = text; return true; }), true, "cut with several cursors");
        Equal(copied, "AZ\r\nBZ\r\nCZ", "their texts to the clipboard");
        Equal(view.All, " two| four|", "and out of the text");
        view.Session.KeyDown(VirtualKey.Right, false, true, false);
        string before = view.All;
        Equal(view.Session.TryCut(text => false), true, "a clipboard that does not take them: still the cursors' cut");
        Equal(view.All, before, "but nothing is taken out");
        view.Session.KeyDown(VirtualKey.Left, false, false, false);
        Equal(view.Session.TryCopyCursors(out List<string> none), false, "no selection: nothing to copy");
        Equal(none.Count, 0, "an empty list");
        Equal(view.Session.TryCut(text => true), false, "and the cut is the view's");
        view.Session.KeyDown(VirtualKey.Right, false, true, false);
        view.Editable = false;
        Equal(view.Session.TryCut(text => true), false, "read-only: the view's, though the cursors have selections");
        Equal(view.Session.TryPaste("x"), false, "a paste too");
        View one = new View("a");
        Equal(one.Session.TryCopyCursors(out List<string> single), false, "one cursor: the view copies");
        Check(single == null, "and the list is not made");
        Equal(one.Session.TryPaste("x"), false, "and pastes");
    }

    private static void ClickChecks()
    {
        View view = new View("abc", "abc", "abc", "abc");
        view.Caret = At(0, 1);
        view.Session.OnCaretClicked(At(0, 0));
        view.Session.AltClick(At(2, 2), false);
        Equal(view.Cursors, "2:2 + 0:1", "Alt+click adds a cursor where it clicks");
        view.Session.AltClick(At(0, 1), false);
        Equal(view.Cursors, "2:2 + -", "on a cursor: takes it away");
        view.Session.AltClick(At(3, 3), true);
        Equal(view.Cursors, "3:1>3:3 + 0:1>0:3 1:1>1:3 2:1>2:3", "Alt+Shift+click: from the last Alt+click (0:1) to it, a block of selections on each line");
        view = new View("abc", "abc", "abc", "abc");
        view.Caret = At(1, 0);
        view.Session.OnCaretClicked(At(0, 0));
        view.Session.AltClick(At(2, 2), true);
        Equal(view.Cursors, "2:0>2:2 + 0:0>0:2 1:0>1:2", "from the last click: a block of selections");
        view = new View("abc", "abc", "abc");
        view.Caret = At(0, 3);
        view.Session.AltClick(At(2, 3), true);
        Equal(view.Cursors, "2:3 + 0:3 1:3", "with no click yet: from the caret");
        Check(view.Take().StartsWith("hide completion; redraw", StringComparison.Ordinal), "the word list closed");
    }

    private static void GoalChecks()
    {
        View view = new View("abcdef", "ab", "abcdef", "ab", "abcdef");
        view.Caret = At(0, 5);
        view.Session.AltClick(At(2, 5), false);
        view.Session.KeyDown(VirtualKey.Down, false, false, false);
        Equal(view.Cursors, "3:2 + 1:2", "Down onto short lines");
        view.Session.KeyDown(VirtualKey.Down, false, false, false);
        Equal(view.Cursors, "4:5 + 2:5", "and on: every cursor goes back to its column, the main one too");
    }

    private static void ApplyingChecks()
    {
        View view = ThreeLines("a", "b", "c");
        Equal(view.Session.IsApplying, false, "not applying between the edits");
        view.Session.TypeCharacter('x', false);
        Check(!view.Take().Contains("not the cursors'"), "the edits of the cursors are theirs: they stay");
        Equal(view.Session.CursorCount, 3, "three cursors after typing");
        view.Session.OnViewChanged();
        Equal(view.Session.HasMultipleCursors, false, "a change of the view of its own takes them away");
        view = ThreeLines("a", "b", "c");
        view.SetCaret(At(1, 0));
        Equal(view.Session.HasMultipleCursors, false, "a caret set by something else takes them away");
    }

    private static void Main()
    {
        CommandChecks();
        TypingChecks();
        KeyChecks();
        ClipboardChecks();
        ClickChecks();
        GoalChecks();
        ApplyingChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " cursor session checks passed.");
    }
}
