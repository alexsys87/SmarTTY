using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmarTTY.CodeEditing;

// Checks of the multi-cursor model (CodeEditing/MultiCursor.cs): moving, adding cursors, and the edits made at several cursors at once,
// including a comparison of the cursors after the edits with an independent computation on the flat text.
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
        Check(EqualityComparer<T>.Default.Equals(actual, expected), name + (EqualityComparer<T>.Default.Equals(actual, expected) ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private sealed class Doc : IMultiCursorText
    {
        public readonly string[] Lines;

        public Doc(params string[] lines)
        {
            Lines = lines;
        }

        public int LineCount => Lines.Length;

        public string GetLine(int line) => Lines[line];
    }

    private static TextPoint P(int line, int column) => new TextPoint(line, column);

    private static TextCursor C(int line, int column) => TextCursor.At(P(line, column));

    private static TextCursor S(int line1, int column1, int line2, int column2) => new TextCursor(P(line1, column1), P(line2, column2));

    private static CursorSet Set(params TextCursor[] cursors) => CursorSet.Create(cursors, cursors.Length - 1);

    // "1:2" for a caret, "1:2-1:5" for a selection that runs forward, "1:5-1:2" for one that runs back.
    private static string Show(CursorSet set)
    {
        return string.Join(" ", set.Items.Select((TextCursor c, int i) => (i == set.Primary ? "*" : "") + (c.HasSelection ? c.Anchor + "-" + c.Caret : c.Caret.ToString())));
    }

    private static string Text(IEnumerable<string> lines) => string.Join("|", lines);

    private static void SetChecks()
    {
        Equal(Show(Set(C(2, 0), C(0, 3), C(1, 1))), "0:3 *1:1 2:0", "the cursors are sorted, the primary one is found again");
        Equal(Show(CursorSet.Create(new[] { C(0, 3), C(0, 3) }, 1)), "*0:3", "two carets at one place are one");
        Equal(Show(CursorSet.Create(new[] { S(0, 1, 0, 5), S(0, 3, 0, 8) }, 0)), "*0:1-0:8", "overlapping selections are merged");
        Equal(Show(CursorSet.Create(new[] { S(0, 1, 0, 5), S(0, 5, 0, 8) }, 0)), "*0:1-0:5 0:5-0:8", "selections that only touch are kept apart");
        Equal(Show(CursorSet.Create(new[] { S(0, 1, 0, 5), C(0, 5) }, 1)), "*0:1-0:5", "a caret at the end of a selection is merged into it");
        Equal(Show(CursorSet.Create(new[] { S(0, 5, 0, 1), C(0, 3) }, 1)), "*0:5-0:1", "a caret inside a selection is merged, the direction is kept");
        Check(CursorSet.Create(new[] { C(0, 0) }, 0).Items.Length == 1, "one cursor");
        bool threw = false;
        try
        {
            CursorSet.Create(new TextCursor[0], 0);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "a set needs a cursor");
    }

    private static void MoveChecks()
    {
        Doc doc = new Doc("hello world", "", "ab", "  indented");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 3), C(2, 1)), CursorMove.Right, false)), "0:4 *2:2", "Right");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 11), C(2, 2)), CursorMove.Right, false)), "1:0 *3:0", "Right at the end of a line goes to the next one");
        Equal(Show(MultiCursor.Move(doc, Set(C(3, 10)), CursorMove.Right, false)), "*3:10", "Right at the end of the text stays");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 0), C(2, 0)), CursorMove.Left, false)), "0:0 *1:0", "Left at the start of a line goes to the end of the previous one, at the start of the text stays");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 8), C(2, 2)), CursorMove.Down, false)), "1:0 *3:2", "Down: a short line takes the caret to its end");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 8)), CursorMove.Up, false)), "*0:0", "Up on the first line goes to its start");
        Equal(Show(MultiCursor.Move(doc, Set(C(3, 4)), CursorMove.Down, false)), "*3:10", "Down on the last line goes to its end");

        // The column is remembered over a short line.
        CursorSet down = MultiCursor.Move(doc, Set(C(0, 8)), CursorMove.Down, false);
        down = MultiCursor.Move(doc, down, CursorMove.Down, false);
        down = MultiCursor.Move(doc, down, CursorMove.Down, false);
        Equal(Show(down), "*3:8", "the goal column comes back after a short line");

        Equal(Show(MultiCursor.Move(doc, Set(S(0, 2, 0, 6)), CursorMove.Right, false)), "*0:6", "Right collapses a selection to its end");
        Equal(Show(MultiCursor.Move(doc, Set(S(0, 2, 0, 6)), CursorMove.Left, false)), "*0:2", "Left collapses a selection to its start");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 2)), CursorMove.Right, true)), "*0:2-0:3", "Shift+Right selects");
        Equal(Show(MultiCursor.Move(doc, Set(S(0, 2, 0, 3)), CursorMove.Left, true)), "*0:2", "Shift+Left shrinks the selection to nothing");
        Equal(Show(MultiCursor.Move(doc, Set(C(3, 5)), CursorMove.Home, false)), "*3:2", "Home goes to the first letter");
        Equal(Show(MultiCursor.Move(doc, Set(C(3, 2)), CursorMove.Home, false)), "*3:0", "...and to the start of the line the second time");
        Equal(Show(MultiCursor.Move(doc, Set(C(3, 0)), CursorMove.Home, false)), "*3:2", "...and back");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 3)), CursorMove.End, true)), "*0:3-0:11", "Shift+End");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 0)), CursorMove.WordRight, false)), "*0:5", "Ctrl+Right: over a word");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 5)), CursorMove.WordRight, false)), "*0:11", "...over the space and the next word");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 11)), CursorMove.WordRight, false)), "*1:0", "...to the next line from the end of a line");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 11)), CursorMove.WordLeft, false)), "*0:6", "Ctrl+Left");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 0)), CursorMove.WordLeft, false)), "*0:0", "...stays at the start of the text");
        Equal(Show(MultiCursor.Move(doc, Set(C(2, 0)), CursorMove.WordLeft, false)), "*1:0", "...goes to the end of the previous line (here an empty one)");

        // Cursors that reach one another are merged.
        Equal(Show(MultiCursor.Move(doc, Set(C(2, 0), C(1, 0)), CursorMove.Right, false)), "*2:0 2:1", "Right: [1:0, 2:0] become [2:0, 2:1]");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 11), C(1, 0)), CursorMove.Left, false)), "0:10 *0:11", "Left: [0:11, 1:0] become [0:10, 0:11]");
        Equal(Show(MultiCursor.Move(doc, Set(C(0, 0), C(0, 1)), CursorMove.Left, false)), "*0:0", "Left: carets at 0:0 and 0:1 meet");
    }

    private static void AddChecks()
    {
        Doc doc = new Doc("alpha", "be", "gamma delta", "", "alpha");
        Equal(Show(MultiCursor.AddOnAdjacentLine(doc, Set(C(0, 4)), below: true)), "0:4 *1:2", "a cursor below, on a shorter line");
        Equal(Show(MultiCursor.AddOnAdjacentLine(doc, Set(C(2, 7)), below: false)), "*1:2 2:7", "a cursor above");
        Equal(Show(MultiCursor.AddOnAdjacentLine(doc, Set(C(0, 1)), below: false)), "*0:1", "none above the first line");
        Equal(Show(MultiCursor.AddOnAdjacentLine(doc, Set(C(4, 1)), below: true)), "*4:1", "none below the last line");
        // The goal column survives a short line: 0:4 -> 1:2 -> 2:4.
        CursorSet three = MultiCursor.AddOnAdjacentLine(doc, MultiCursor.AddOnAdjacentLine(doc, Set(C(0, 4)), true), true);
        Equal(Show(three), "0:4 1:2 *2:4", "the column is kept over a short line");
        Equal(Show(MultiCursor.AddOnAdjacentLine(doc, Set(C(0, 4), C(2, 5)), below: true)), "0:4 2:5 *3:0", "below the last cursor");

        Equal(Show(MultiCursor.ToggleAt(doc, Set(C(0, 1)), P(2, 3))), "0:1 *2:3", "Alt+click adds a cursor");
        Equal(Show(MultiCursor.ToggleAt(doc, Set(C(0, 1), C(2, 3)), P(2, 3))), "*0:1", "...and takes it away on a second click");
        Equal(Show(MultiCursor.ToggleAt(doc, Set(C(0, 1)), P(0, 1))), "*0:1", "...but never the last one");
        Equal(Show(MultiCursor.ToggleAt(doc, Set(C(0, 1)), P(1, 50))), "0:1 *1:2", "a click beyond the end of a line is clamped");
        Equal(Show(MultiCursor.ToggleAt(doc, Set(S(0, 0, 0, 3), C(2, 0)), P(0, 2))), "*2:0", "a click inside a selection takes it away");

        // Ctrl+D.
        CursorSet word = MultiCursor.AddNextOccurrence(doc, Set(C(0, 2)));
        Equal(Show(word), "*0:0-0:5", "Ctrl+D on a word selects it");
        Check(word.WholeWord, "...for whole words from then on");
        CursorSet next = MultiCursor.AddNextOccurrence(doc, word);
        Equal(Show(next), "0:0-0:5 *4:0-4:5", "Ctrl+D again adds the next place");
        CursorSet wrap = MultiCursor.AddNextOccurrence(doc, next);
        Equal(Show(wrap), "0:0-0:5 *4:0-4:5", "...and when there is no more, nothing happens");
        Equal(Show(MultiCursor.AddNextOccurrence(doc, Set(C(3, 0)))), "*3:0", "Ctrl+D on an empty line does nothing");

        Doc sub = new Doc("foo foobar foo", "xfoo");
        CursorSet fooWord = MultiCursor.AddNextOccurrence(sub, Set(C(0, 1)));
        fooWord = MultiCursor.AddNextOccurrence(sub, fooWord);
        Equal(Show(fooWord), "0:0-0:3 *0:11-0:14", "a word selected from the caret finds whole words only");
        CursorSet fooPart = MultiCursor.AddNextOccurrence(sub, Set(S(0, 0, 0, 3)));
        fooPart = MultiCursor.AddNextOccurrence(sub, fooPart);
        Equal(Show(fooPart), "0:0-0:3 0:4-0:7 *0:11-0:14", "a selection that was made by hand (two Ctrl+D) finds the text anywhere");
        fooPart = MultiCursor.AddNextOccurrence(sub, MultiCursor.AddNextOccurrence(sub, fooPart));
        Equal(Show(fooPart), "0:0-0:3 0:4-0:7 0:11-0:14 *1:1-1:4", "...also inside a word of another line");
        Equal(Show(MultiCursor.AddNextOccurrence(sub, fooPart)), Show(fooPart), "...and then it stops");
        // The search starts after the primary cursor and wraps round.
        Equal(Show(MultiCursor.AddNextOccurrence(sub, Set(S(1, 1, 1, 4)))), "*0:0-0:3 1:1-1:4", "from the last place round to the first one (the search wraps)");
        Doc multi = new Doc("ab", "cd");
        Equal(Show(MultiCursor.AddNextOccurrence(multi, Set(S(0, 1, 1, 1)))), "*0:1-1:1", "a selection over a line break has no next place");

        // Ctrl+Shift+L.
        Equal(Show(MultiCursor.SelectAllOccurrences(doc, Set(C(4, 2)))), "0:0-0:5 *4:0-4:5", "all the places of a word (the primary one is the one under the caret)");
        Equal(Show(MultiCursor.SelectAllOccurrences(sub, Set(S(0, 0, 0, 3)))), "*0:0-0:3 0:4-0:7 0:11-0:14 1:1-1:4", "all the places of a selected text, also inside words");
        Equal(Show(MultiCursor.SelectAllOccurrences(doc, Set(C(3, 0)))), "*3:0", "nothing to select on an empty line");
        Equal(MultiCursor.SelectAllOccurrences(new Doc(new string('a', 1) + " " + string.Join(" ", Enumerable.Repeat("a", 6000))), Set(C(0, 0))).Count, CursorSet.MaxCursors, "no more cursors than the limit");

        // Alt+Shift+click.
        Doc grid = new Doc("abcdef", "ab", "abcdef", "");
        Equal(Show(MultiCursor.SelectColumns(grid, P(0, 2), P(2, 4))), "0:2-0:4 1:2 *2:2-2:4", "a rectangle: the short line gets a caret at its end");
        Equal(Show(MultiCursor.SelectColumns(grid, P(2, 3), P(0, 3))), "*0:3 1:2 2:3", "a rectangle drawn upwards, the primary cursor is on the clicked line");
        Equal(MultiCursor.SelectColumns(grid, P(0, 1), P(3, 1)).Count, 4, "a line of the rectangle that is empty has its cursor too");
    }

    private static void ReadChecks()
    {
        Doc doc = new Doc("one two", "three", "four");
        Equal(Text(MultiCursor.SelectedTexts(doc, Set(S(0, 0, 0, 3), C(1, 2), S(1, 1, 2, 2)))), "one|hree\nfo", "the selected texts, a selection over a line break has \\n in it");
        Equal(MultiCursor.GetRange(doc, P(0, 4), P(2, 2)), "two\nthree\nfo", "a range over lines");
        Equal(MultiCursor.GetRange(doc, P(1, 1), P(1, 99)), "hree", "a range is clamped to the line");

        Equal(string.Join("|", MultiCursor.SplitForCursors("a\nb\nc", 3)), "a|b|c", "a clipboard with a line for each cursor is split");
        Equal(string.Join("|", MultiCursor.SplitForCursors("a\r\nb\r\n", 2)), "a|b", "...also with CRLF and a line break at the end");
        Check(MultiCursor.SplitForCursors("a\nb", 3) == null, "...but not when the numbers differ");
        Check(MultiCursor.SplitForCursors("a\nb", 1) == null, "...nor for one cursor");
        Check(MultiCursor.SplitForCursors("", 2) == null, "...nor an empty clipboard");
    }

    private static void EditChecks()
    {
        Doc doc = new Doc("one two three", "four five", "six");
        EditPlan insert = MultiCursor.Insert(doc, Set(C(0, 0), C(1, 0), C(2, 3)), "# ");
        Equal(Text(MultiCursor.Apply(doc.Lines, insert)), "# one two three|# four five|six# ", "the same text at three carets");
        Equal(Show(insert.Result), "0:2 1:2 *2:5", "...and the carets behind it");

        EditPlan replace = MultiCursor.Insert(doc, Set(S(0, 4, 0, 7), S(0, 8, 0, 13)), "X");
        Equal(Text(MultiCursor.Apply(doc.Lines, replace)), "one X X|four five|six", "selections on one line are replaced");
        Equal(Show(replace.Result), "0:5 *0:7", "...the second caret is moved by the first edit (0:5, not 0:9)");

        EditPlan nl = MultiCursor.Insert(doc, Set(C(0, 3), C(0, 7)), "\n");
        Equal(Text(MultiCursor.Apply(doc.Lines, nl)), "one| two| three|four five|six", "line breaks at two places of a line");
        Equal(Show(nl.Result), "1:0 *2:0", "...the carets are at the start of the new lines");

        EditPlan enter = MultiCursor.NewLine(new Doc("  a b", "\tc d"), Set(C(0, 3), C(1, 2)), "\n");
        Equal(Text(MultiCursor.Apply(new Doc("  a b", "\tc d").Lines, enter)), "  a|   b|\tc|\t d", "Enter keeps the indentation of the line");
        Equal(Show(enter.Result), "1:2 *3:1", "...the carets stand after the indentation");
        EditPlan inside = MultiCursor.NewLine(new Doc("    x"), Set(C(0, 2)), "\n");
        Equal(Text(MultiCursor.Apply(new Doc("    x").Lines, inside)), "  |    x", "Enter inside the indentation carries only the part in front of the caret");

        EditPlan tab = MultiCursor.Tab(new Doc("a", "abc", "\tx"), Set(C(0, 1), C(1, 3), C(2, 1)), 4, true);
        Equal(Text(MultiCursor.Apply(new Doc("a", "abc", "\tx").Lines, tab)), "a   |abc |\t    x", "Tab with spaces goes to the next tab stop of each line");
        EditPlan tabChar = MultiCursor.Tab(doc, Set(C(0, 0), C(2, 0)), 4, false);
        Equal(Text(MultiCursor.Apply(doc.Lines, tabChar)), "\tone two three|four five|\tsix", "Tab as a tab character");

        Doc bs = new Doc("abc", "def", "    ghi");
        EditPlan back = MultiCursor.Backspace(bs, Set(C(0, 2), C(1, 0), C(1, 3)));
        Equal(Text(MultiCursor.Apply(bs.Lines, back)), "acde|    ghi", "Backspace: a letter, a line break, a letter");
        EditPlan back2 = MultiCursor.Backspace(new Doc("abc", "def"), Set(C(0, 1), C(1, 0)));
        Equal(Text(MultiCursor.Apply(new Doc("abc", "def").Lines, back2)), "bcdef", "Backspace joins a line with the one above");
        Equal(Show(back2.Result), "0:0 *0:2", "...and the caret stands where the lines meet");
        // Joining two lines takes the indentation of the second one along, as the editor does.
        Doc indented = new Doc("  a", "  b", "    c");
        EditPlan joinIndented = MultiCursor.Backspace(indented, Set(C(1, 0), C(2, 0)));
        Equal(Text(MultiCursor.Apply(indented.Lines, joinIndented)), "  ab  c", "Backspace at the start of an indented line: the common indentation goes");
        Equal(Show(joinIndented.Result), "0:3 *0:4", "...and the carets stand where the lines met");
        EditPlan joinDelete = MultiCursor.Delete(indented, Set(C(0, 3)));
        Equal(Text(MultiCursor.Apply(indented.Lines, joinDelete)), "  ab|    c", "Delete at the end of a line does the same");

        // Two carets in one word: the two Ctrl+Backspace overlap and become one deletion.
        Doc oneWord = new Doc("abcdef");
        EditPlan overlap = MultiCursor.DeleteWord(oneWord, Set(C(0, 2), C(0, 5)), forward: false);
        Equal(Text(MultiCursor.Apply(oneWord.Lines, overlap)), "f", "Ctrl+Backspace at two carets of one word");
        Equal(Show(overlap.Result), "*0:0", "...leaves one caret");
        EditPlan backSpaces = MultiCursor.Backspace(bs, Set(C(2, 4)), spaceIndent: 4);
        Equal(Text(MultiCursor.Apply(bs.Lines, backSpaces)), "abc|def|ghi", "Backspace in space indentation goes back to the tab stop");
        EditPlan backSpaces2 = MultiCursor.Backspace(new Doc("      x"), Set(C(0, 6)), spaceIndent: 4);
        Equal(Text(MultiCursor.Apply(new Doc("      x").Lines, backSpaces2)), "    x", "...by two when it is in the middle of a level");
        EditPlan backAtStart = MultiCursor.Backspace(bs, Set(C(0, 0)));
        Equal(Text(MultiCursor.Apply(bs.Lines, backAtStart)), Text(bs.Lines), "Backspace at the start of the text does nothing");
        Equal(Show(backAtStart.Result), "*0:0", "...and the caret stays");
        EditPlan backSel = MultiCursor.Backspace(bs, Set(S(0, 1, 1, 1), C(2, 5)));
        Equal(Text(MultiCursor.Apply(bs.Lines, backSel)), "aef|    hi", "Backspace with a selection over lines");
        EditPlan backEmoji = MultiCursor.Backspace(new Doc("a\U0001F600b"), Set(C(0, 3)));
        Equal(Text(MultiCursor.Apply(new Doc("a\U0001F600b").Lines, backEmoji)), "ab", "Backspace takes a whole surrogate pair");

        EditPlan del = MultiCursor.Delete(bs, Set(C(0, 1), C(0, 3), C(1, 3)));
        Equal(Text(MultiCursor.Apply(bs.Lines, del)), "acdef    ghi", "Delete: a letter and two line breaks");
        EditPlan delEnd = MultiCursor.Delete(new Doc("ab"), Set(C(0, 2)));
        Equal(Text(MultiCursor.Apply(new Doc("ab").Lines, delEnd)), "ab", "Delete at the end of the text does nothing");

        Doc words = new Doc("alpha beta gamma", "delta epsilon");
        EditPlan wordBack = MultiCursor.DeleteWord(words, Set(C(0, 10), C(1, 5)), forward: false);
        Equal(Text(MultiCursor.Apply(words.Lines, wordBack)), "alpha  gamma| epsilon", "Ctrl+Backspace deletes the word before each caret");
        Equal(Show(wordBack.Result), "0:6 *1:0", "...");
        EditPlan wordFwd = MultiCursor.DeleteWord(words, Set(C(0, 6), C(1, 6)), forward: true);
        Equal(Text(MultiCursor.Apply(words.Lines, wordFwd)), "alpha  gamma|delta ", "Ctrl+Delete deletes the word after each caret");

        EditPlan cut = MultiCursor.DeleteSelections(words, Set(S(0, 0, 0, 6), C(0, 12), S(1, 0, 1, 6)));
        Equal(Text(MultiCursor.Apply(words.Lines, cut)), "beta gamma|epsilon", "Cut removes the selections and leaves the carets alone");
        Equal(Show(cut.Result), "0:0 0:6 *1:0", "...the caret that had no selection is moved by the first edit");

        string[] parts = { "1", "2", "3" };
        EditPlan paste = MultiCursor.InsertPerCursor(doc, Set(C(0, 0), C(1, 0), C(2, 0)), parts);
        Equal(Text(MultiCursor.Apply(doc.Lines, paste)), "1one two three|2four five|3six", "a line of the clipboard for each cursor");
        EditPlan multiLine = MultiCursor.InsertPerCursor(doc, Set(C(0, 0), C(2, 0)), new[] { "a\nb", "c\nd" });
        Equal(Text(MultiCursor.Apply(doc.Lines, multiLine)), "a|bone two three|four five|c|dsix", "a text with line breaks at two carets");
        Equal(Show(multiLine.Result), "1:1 *4:1", "...the carets are behind the inserted text, below the first insertion");

        bool threw = false;
        try
        {
            MultiCursor.InsertPerCursor(doc, Set(C(0, 0), C(1, 0)), parts);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Check(threw, "the number of texts must match the number of cursors");
    }

    // ---- random checks against the flat text -----------------------------------------------------------------------

    private static int Offset(string[] lines, TextPoint point)
    {
        int offset = 0;
        for (int i = 0; i < point.Line; i++)
        {
            offset += lines[i].Length + 1;
        }
        return offset + point.Column;
    }

    private static TextPoint PointOf(string[] lines, int offset)
    {
        for (int line = 0; line < lines.Length; line++)
        {
            if (offset <= lines[line].Length)
            {
                return new TextPoint(line, offset);
            }
            offset -= lines[line].Length + 1;
        }
        throw new Exception("offset beyond the text");
    }

    private static string Flat(IEnumerable<string> lines) => string.Join("\n", lines);

    private static void RandomChecks()
    {
        Random random = new Random(12345);
        string alphabet = "ab c\tdé_.";
        int checkedPlans = 0;
        for (int round = 0; round < 3000; round++)
        {
            int lineCount = random.Next(1, 6);
            string[] lines = new string[lineCount];
            for (int i = 0; i < lineCount; i++)
            {
                StringBuilder line = new StringBuilder();
                int length = random.Next(0, 8);
                for (int j = 0; j < length; j++)
                {
                    line.Append(alphabet[random.Next(alphabet.Length)]);
                }
                lines[i] = line.ToString();
            }
            Doc doc = new Doc(lines);
            string flat = Flat(lines);

            // Random cursors: carets and selections; they are merged by Create, and the edits are made on what is left.
            int cursorCount = random.Next(1, 6);
            List<TextCursor> cursors = new List<TextCursor>();
            for (int i = 0; i < cursorCount; i++)
            {
                TextPoint a = PointOf(lines, random.Next(0, flat.Length + 1));
                TextPoint b = random.Next(3) == 0 ? PointOf(lines, random.Next(0, flat.Length + 1)) : a;
                cursors.Add(new TextCursor(a, b));
            }
            CursorSet set = CursorSet.Create(cursors, random.Next(cursors.Count));

            EditPlan plan;
            string op;
            switch (random.Next(6))
            {
                case 0:
                    op = "insert";
                    plan = MultiCursor.Insert(doc, set, random.Next(3) == 0 ? "" : "xy");
                    break;
                case 1:
                    op = "insert lines";
                    plan = MultiCursor.Insert(doc, set, "p\nq\n");
                    break;
                case 2:
                    op = "backspace";
                    plan = MultiCursor.Backspace(doc, set);
                    break;
                case 3:
                    op = "delete";
                    plan = MultiCursor.Delete(doc, set);
                    break;
                case 4:
                    op = "enter";
                    plan = MultiCursor.NewLine(doc, set, "\n");
                    break;
                default:
                    op = "delete word";
                    plan = MultiCursor.DeleteWord(doc, set, random.Next(2) == 0);
                    break;
            }

            // The edits do not overlap and are in the order of the text.
            for (int i = 0; i < plan.Edits.Count; i++)
            {
                if (plan.Edits[i].Start > plan.Edits[i].End || (i > 0 && plan.Edits[i].Start < plan.Edits[i - 1].End))
                {
                    throw new Exception("FAIL: overlapping edits for " + op + " in '" + Text(lines) + "' with " + Show(set));
                }
            }

            // Reference: the same edits on the flat text, by offsets, last first.
            StringBuilder reference = new StringBuilder(flat);
            List<int> expectedCaretOffsets = new List<int>();
            int shift = 0;
            foreach (TextEdit edit in plan.Edits)
            {
                int start = Offset(lines, edit.Start);
                int end = Offset(lines, edit.End);
                string text = edit.Text.Replace("\r\n", "\n");
                expectedCaretOffsets.Add(start + shift + text.Length);
                shift += text.Length - (end - start);
            }
            for (int i = plan.Edits.Count - 1; i >= 0; i--)
            {
                int start = Offset(lines, plan.Edits[i].Start);
                int end = Offset(lines, plan.Edits[i].End);
                reference.Remove(start, end - start);
                reference.Insert(start, plan.Edits[i].Text);
            }

            List<string> applied = MultiCursor.Apply(lines, plan);
            if (Flat(applied) != reference.ToString())
            {
                throw new Exception("FAIL: " + op + " in '" + Text(lines) + "' with " + Show(set) + ": got '" + Text(applied) + "', expected '" + reference.ToString().Replace("\n", "|") + "'");
            }

            string[] newLines = applied.ToArray();
            List<int> expected = expectedCaretOffsets.Distinct().OrderBy(x => x).ToList();
            List<int> actual = plan.Result.Items.Select(c => Offset(newLines, c.Caret)).ToList();
            if (!expected.SequenceEqual(actual) || plan.Result.Items.Any(c => c.HasSelection))
            {
                throw new Exception("FAIL: cursors after " + op + " in '" + Text(lines) + "' with " + Show(set) + ": got " + string.Join(",", actual) + ", expected " + string.Join(",", expected));
            }
            checkedPlans++;
        }
        Check(checkedPlans == 3000, "3000 random edits: the text and the cursors agree with the flat text");
    }

    private static void RandomMoveChecks()
    {
        Random random = new Random(777);
        for (int round = 0; round < 2000; round++)
        {
            int lineCount = random.Next(1, 5);
            string[] lines = Enumerable.Range(0, lineCount).Select(_ => new string('a', random.Next(0, 6))).ToArray();
            Doc doc = new Doc(lines);
            string flat = Flat(lines);
            List<TextCursor> cursors = new List<TextCursor>();
            for (int i = 0; i < random.Next(1, 5); i++)
            {
                TextPoint p = PointOf(lines, random.Next(0, flat.Length + 1));
                cursors.Add(TextCursor.At(p));
            }
            CursorSet set = CursorSet.Create(cursors, 0);
            foreach (CursorMove move in Enum.GetValues(typeof(CursorMove)))
            {
                CursorSet moved = MultiCursor.Move(doc, set, move, random.Next(2) == 0);
                for (int i = 0; i < moved.Count; i++)
                {
                    TextCursor c = moved.Items[i];
                    if (c.Caret.Line < 0 || c.Caret.Line >= lineCount || c.Caret.Column < 0 || c.Caret.Column > lines[c.Caret.Line].Length
                        || (i > 0 && c.Start < moved.Items[i - 1].End))
                    {
                        throw new Exception("FAIL: " + move + " from " + Show(set) + " in '" + Text(lines) + "' gave " + Show(moved));
                    }
                }
            }
        }
        Check(true, "2000 random moves stay inside the text and keep the cursors in order");
    }

    private static void Main()
    {
        SetChecks();
        MoveChecks();
        AddChecks();
        ReadChecks();
        EditChecks();
        RandomChecks();
        RandomMoveChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " multi-cursor checks passed.");
    }
}
