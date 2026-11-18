using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the part of the line commands of the editor that needs no control (CodeEditing/LineEditing.cs): which lines a command works on,
// the smallest replacements between old and new lines, where a column goes, the text of a duplicate and of a move; and of the text the
// completion works on (CodeEditing/AnalysisText.cs). Random inputs are run through the code and every answer is written down;
// LineEditingGolden.txt and AnalysisGolden.txt are what the same inputs gave when this code was still part of the editor control
// (CodeEditorPP.LineEditing.cs and GetAnalysisText), and the answers must stay the same.
// Run with: dotnet run --project tests/LineEditingTests
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

    private static TextDocument.TextPosition P(int line, int column)
    {
        return new TextDocument.TextPosition(column, line);
    }

    // ---- the script of random inputs: the same for the golden file and for the checks

    private static string Quote(string s) { return "'" + s.Replace("\t", "\\t") + "'"; }

    private static string RandomText(Random r, int max)
    {
        string alphabet = "ab  \t(){}";
        StringBuilder sb = new StringBuilder();
        int n = r.Next(0, max + 1);
        for (int i = 0; i < n; i++)
        {
            sb.Append(alphabet[r.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }

    private static string Mutate(Random r, string s)
    {
        switch (r.Next(6))
        {
            case 0: return s;
            case 1: return RandomText(r, 8);
            case 2: return s.Length == 0 ? "x" : s.Insert(r.Next(s.Length + 1), RandomText(r, 3));
            case 3:
                if (s.Length == 0)
                {
                    return s;
                }
                int a = r.Next(s.Length);
                int l = r.Next(1, Math.Min(3, s.Length - a) + 1);
                return s.Remove(a, l);
            case 4: return "  " + s;
            default: return s.TrimStart();
        }
    }

    private static string[] Slice(string[] lines, int first, int last)
    {
        return lines.Skip(first).Take(last - first + 1).ToArray();
    }

    private static string EditsText(int first, string[] oldLines, string[] newLines)
    {
        return string.Join(";", LineEditing.DiffLines(first, oldLines, newLines).Select(e => e.Start.Line + ":" + e.Start.Column + "-" + e.End.Line + ":" + e.End.Column + "='" + e.Text + "'"));
    }

    private static string RunScript(int seed, int cases)
    {
        Random r = new Random(seed);
        StringBuilder sb = new StringBuilder();
        for (int c = 0; c < cases; c++)
        {
            int n = r.Next(1, 8);
            string[] doc = Enumerable.Range(0, n).Select(i => RandomText(r, 8)).ToArray();
            int caretLine = r.Next(0, n);
            bool hasSel = r.Next(3) != 0;
            // The column is drawn before the line: the order of the draws is that of the golden file.
            int selColumn = r.Next(0, doc[Math.Min(n - 1, r.Next(0, n))].Length + 2);
            int selLine = r.Next(0, n);
            TextDocument.TextPosition selStart = P(selLine, selColumn);
            int caretColumn = r.Next(0, 3) == 0 ? 0 : r.Next(0, 9);
            TextDocument.TextPosition caret = P(caretLine, caretColumn);
            sb.AppendLine("#" + c + " doc=" + string.Join("|", doc.Select(Quote)) + " caretLine=" + caretLine + " sel=" + hasSel + " start=" + selStart.Line + ":" + selStart.Column + " caret=" + caret.Line + ":" + caret.Column);
            LineEditing.GetCommandLines(caretLine, hasSel, selStart, caret, out int first, out int last);
            sb.AppendLine("  lines: " + first + "," + last + " hasLineSel=" + LineEditing.HasLineSelection(caretLine, hasSel, selStart, caret));
            foreach (LineTransformKind kind in Enum.GetValues(typeof(LineTransformKind)))
            {
                bool ok = LineEditing.TryGetTransformLines(kind, n, caretLine, hasSel, selStart, caret, out int f, out int l);
                sb.Append("  " + kind + ": " + (ok ? "ok " + f + "," + l : "no") + ";");
            }
            sb.AppendLine();
            string[] changed = doc.Select(line => Mutate(r, line)).ToArray();
            sb.AppendLine("  edits: " + EditsText(0, doc, changed));
            for (int i = 0; i < n; i++)
            {
                sb.Append("  shift[" + i + "]: ");
                foreach (int col in new[] { 0, 1, 3, 6, 12 })
                {
                    sb.Append(LineEditing.ShiftColumn(P(i, col), 0, doc, changed) + ",");
                }
                int prefix = LineEditing.CommonPrefixLength(doc[i], changed[i]);
                sb.AppendLine(" prefix=" + prefix + " suffix=" + LineEditing.CommonSuffixLength(doc[i], changed[i], prefix));
            }
            int from = r.Next(0, n);
            int to = r.Next(from, n);
            sb.AppendLine("  duplicated[" + from + "," + to + "]: " + Quote(LineEditing.DuplicatedText(Slice(doc, from, to), "\n")));
            foreach (int direction in new[] { -1, 1 })
            {
                if ((direction < 0 && from == 0) || (direction > 0 && to >= n - 1))
                {
                    sb.AppendLine("  moved[" + direction + "]: -");
                    continue;
                }
                int neighbor = direction < 0 ? from - 1 : to + 1;
                sb.AppendLine("  moved[" + direction + "]: " + Quote(LineEditing.MovedText(Slice(doc, from, to), doc[neighbor], "\n", direction)));
            }
        }
        return sb.ToString();
    }

    private static string Normalize(string text) { return text.Replace("\r\n", "\n"); }

    private static void CompareWithGolden(string actual, string goldenFile, string name)
    {
        string expected = Normalize(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, goldenFile)));
        actual = Normalize(actual);
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            if (expectedLines[i] != actualLines[i])
            {
                throw new Exception("FAIL: " + name + ": line " + (i + 1) + " differs\n  expected: " + expectedLines[i] + "\n  actual:   " + actualLines[i]);
            }
        }
        Check(expectedLines.Length == actualLines.Length && actual == expected, name + " (" + expectedLines.Length + " lines)");
    }

    private static void GoldenChecks()
    {
        CompareWithGolden(RunScript(11, 300), "LineEditingGolden.txt", "300 random cases of the line commands give the answers they gave in the editor control");
    }

    // ---- the lines of a command

    private static void CommandLineChecks()
    {
        LineEditing.GetCommandLines(5, false, P(0, 0), P(5, 3), out int first, out int last);
        Check(first == 5 && last == 5, "without a selection the command works on the line of the caret");
        LineEditing.GetCommandLines(7, true, P(3, 2), P(7, 4), out first, out last);
        Check(first == 3 && last == 7, "a selection from the top down covers its lines");
        LineEditing.GetCommandLines(3, true, P(7, 4), P(3, 2), out first, out last);
        Check(first == 3 && last == 7, "a selection from the bottom up covers the same lines");
        LineEditing.GetCommandLines(7, true, P(3, 2), P(7, 0), out first, out last);
        Check(first == 3 && last == 6, "a selection that ends at the start of a line does not include that line");
        LineEditing.GetCommandLines(4, true, P(4, 5), P(4, 0), out first, out last);
        Check(first == 4 && last == 4, "a selection inside one line, ending at its start, still works on that line");
        LineEditing.GetCommandLines(2, true, P(2, 0), P(2, 0), out first, out last);
        Check(first == 2 && last == 2, "an empty selection is the line of the caret");

        Check(!LineEditing.HasLineSelection(3, false, P(0, 0), P(3, 0)), "no selection is not a selection of lines");
        Check(!LineEditing.HasLineSelection(3, true, P(3, 1), P(3, 5)), "a selection inside a line is not a selection of lines");
        Check(!LineEditing.HasLineSelection(4, true, P(3, 1), P(4, 0)), "two lines whose second is only touched at its start are one line");
        Check(LineEditing.HasLineSelection(4, true, P(3, 1), P(4, 2)), "a selection into a second line is a selection of lines");

        // The Text menu: the commands that need two lines refuse less; the others take the whole text when nothing is selected.
        Check(!LineEditing.TryGetTransformLines(LineTransformKind.SortAscending, 10, 3, false, P(0, 0), P(3, 0), out first, out last), "sorting needs a selection");
        Check(!LineEditing.TryGetTransformLines(LineTransformKind.RemoveDuplicateLines, 10, 3, true, P(3, 1), P(3, 4), out first, out last), "removing duplicates needs two lines");
        Check(LineEditing.TryGetTransformLines(LineTransformKind.SortDescending, 10, 6, true, P(2, 1), P(6, 4), out first, out last) && first == 2 && last == 6, "sorting works on the selected lines");
        Check(LineEditing.TryGetTransformLines(LineTransformKind.TrimTrailingWhitespace, 10, 3, false, P(0, 0), P(3, 0), out first, out last) && first == 0 && last == 9, "trimming takes the whole text when nothing is selected");
        Check(LineEditing.TryGetTransformLines(LineTransformKind.IndentationToTabs, 10, 3, true, P(3, 1), P(3, 4), out first, out last) && first == 3 && last == 3, "converting the indentation takes the selected line");
    }

    // ---- the replacements between lines

    private static string[] ApplyEdits(string[] lines, List<LineEditing.TextEdit> edits)
    {
        string[] result = (string[])lines.Clone();
        foreach (LineEditing.TextEdit edit in edits)
        {
            string line = result[edit.Start.Line];
            result[edit.Start.Line] = line.Substring(0, edit.Start.Column) + edit.Text + line.Substring(edit.End.Column);
        }
        return result;
    }

    private static void EditChecks()
    {
        string[] oldLines = { "a = 1", "  b", "", "tail" };
        string[] newLines = { "a = 2", "  b", "new", "tail  " };
        List<LineEditing.TextEdit> edits = LineEditing.DiffLines(10, oldLines, newLines);
        Equal(edits.Count, 3, "only the lines that differ are replaced");
        Check(edits[0].Start.Line == 10 && edits[0].Start.Column == 4 && edits[0].End.Column == 5 && edits[0].Text == "2", "the part between the common beginning and the common end is replaced, on its own line");
        Check(edits[1].Start.Line == 12 && edits[1].Start.Column == 0 && edits[1].End.Column == 0 && edits[1].Text == "new", "an empty line gets a text by an insertion");
        Check(edits[2].Start.Line == 13 && edits[2].Start.Column == 4 && edits[2].End.Column == 4 && edits[2].Text == "  ", "added blanks at the end are an insertion at the end");
        Check(LineEditing.DiffLines(0, oldLines, oldLines).Count == 0, "no change, no edit");

        // Applying the replacements turns the old lines into the new ones, whatever they are.
        Random random = new Random(3);
        int applied = 0;
        for (int i = 0; i < 4000; i++)
        {
            string[] before = Enumerable.Range(0, random.Next(1, 6)).Select(x => RandomText(random, 10)).ToArray();
            string[] after = before.Select(l => Mutate(random, l)).ToArray();
            if (!ApplyEdits(before, LineEditing.DiffLines(0, before, after)).SequenceEqual(after))
            {
                throw new Exception("FAIL: the replacements do not turn [" + string.Join("|", before) + "] into [" + string.Join("|", after) + "]");
            }
            applied++;
        }
        Check(applied == 4000, "4000 random changes of lines: applying the replacements gives exactly the new lines");

        // The columns of the caret.
        string[] before2 = { "    foo", "bar" };
        string[] after2 = { "foo", "bar" };
        Equal(LineEditing.ShiftColumn(P(0, 2), 0, before2, after2), 0, "a column inside the removed indentation goes to where the change starts");
        Equal(LineEditing.ShiftColumn(P(0, 6), 0, before2, after2), 2, "a column behind the change moves left by the removed blanks");
        Equal(LineEditing.ShiftColumn(P(1, 2), 0, before2, after2), 2, "a line that did not change keeps the column");
        Equal(LineEditing.ShiftColumn(P(5, 3), 0, before2, after2), 3, "a position outside the lines keeps its column");
        Equal(LineEditing.ShiftColumn(P(0, 0), 0, before2, after2), 0, "a column in front of the change stays");
        string[] before3 = { "ab" };
        string[] after3 = { "abXY" };
        Equal(LineEditing.ShiftColumn(P(0, 1), 0, before3, after3), 1, "a column in front of the insertion stays");
        Equal(LineEditing.ShiftColumn(P(0, 2), 0, before3, after3), 2, "a column at the start of an insertion stays");
        Equal(LineEditing.CommonPrefixLength("abc", "abd"), 2, "common beginning");
        Equal(LineEditing.CommonSuffixLength("xabc", "yabc", 0), 3, "common end");
        Equal(LineEditing.CommonSuffixLength("aa", "aaa", 2), 0, "the common end does not reach into the common beginning");
    }

    // ---- the text of a duplicate and of a move

    private static void TextChecks()
    {
        Equal(LineEditing.DuplicatedText(new[] { "a", "b" }, "\r\n"), "\r\na\r\nb", "a duplicate is each line with a line break in front");
        Equal(LineEditing.DuplicatedText(new[] { "" }, "\n"), "\n", "an empty line duplicates as a line break");
        Equal(LineEditing.MovedText(new[] { "b", "c" }, "a", "\n", -1), "b\nc\na", "a block moved up goes in front of the line it swaps with");
        Equal(LineEditing.MovedText(new[] { "a", "b" }, "c", "\n", 1), "c\na\nb", "a block moved down goes behind it");
        Equal(LineEditing.MovedText(new[] { "x" }, "", "\r\n", 1), "\r\nx", "an empty neighbour keeps its place as an empty line");
    }

    // ---- the text for the analysis

    private static string Hash(string text)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 16);
        }
    }

    private static void AnalysisChecks()
    {
        // The documents of the golden file: whole texts, windows around the caret and one huge line.
        Random r = new Random(5);
        StringBuilder script = new StringBuilder();
        for (int c = 0; c < 60; c++)
        {
            StringBuilder sb = new StringBuilder();
            int total = 0;
            int target = r.Next(0, 3) == 0 ? r.Next(1, 5000) : r.Next(300000, 800000);
            bool oneHuge = r.Next(8) == 0;
            if (oneHuge)
            {
                sb.Append(new string('x', r.Next(499000, 700000)));
            }
            else
            {
                while (total < target)
                {
                    int len = r.Next(0, r.Next(3) == 0 ? 3000 : 80);
                    sb.Append(new string('a', len)).Append(r.Next(5) == 0 ? "\r\n" : "\n");
                    total += len + 1;
                }
            }
            EditableTextDocument doc = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
            doc.SetText(sb.ToString());
            int line = r.Next(-1, doc.LineCount + 2);
            int col = r.Next(0, 100000);
            string text = AnalysisText.Get(doc, line, col, out int caretLine, out int caretColumn);
            script.AppendLine(c + " lines=" + doc.LineCount + " chars=" + sb.Length + " caret=" + line + ":" + col + " -> len=" + text.Length + " caret=" + caretLine + ":" + caretColumn + " hash=" + Hash(text));
        }
        CompareWithGolden(script.ToString(), "AnalysisGolden.txt", "60 documents around the analysis budget give the text and the caret they gave in the editor control");

        // A few cases with answers that can be read.
        EditableTextDocument small = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        small.SetText("one\ntwo\nthree");
        string whole = AnalysisText.Get(small, 1, 2, out int line1, out int column1);
        Check(whole == "one\ntwo\nthree" && line1 == 1 && column1 == 2, "a small document is given whole, with the caret where it is");

        EditableTextDocument many = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        many.SetText(string.Join("\n", Enumerable.Range(0, 20000).Select(i => new string((char)('a' + i % 26), 49))));
        string window = AnalysisText.Get(many, 10000, 7, out int windowLine, out int windowColumn);
        Check(window.Length >= AnalysisText.BudgetChars && window.Length <= AnalysisText.BudgetChars + 100, "a large document is given as a window of about the budget (" + window.Length + " characters)");
        string[] windowLines = window.Split('\n');
        Check(windowLines[windowLine] == many.GetLineText(10000) && windowColumn == 7, "the caret line of the window is the caret line of the document, the column is the same");
        Check(Math.Abs(windowLines.Length - 2 * windowLine) <= 2, "the window is the same number of lines above the caret as below it");

        string huge = new string('q', AnalysisText.BudgetChars + 5000);
        EditableTextDocument hugeLine = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        hugeLine.SetText(huge);
        string part = AnalysisText.Get(hugeLine, 0, 400000, out int hugeLineNumber, out int hugeColumn);
        Check(part.Length == AnalysisText.BudgetChars && hugeLineNumber == 0 && hugeColumn == 400000 - 5000, "one huge line is given as the part around the caret, and the column follows");
        string startPart = AnalysisText.Get(hugeLine, 0, 10, out _, out int startColumn);
        Check(startPart.Length == AnalysisText.BudgetChars && startColumn == 10, "a caret near the start of a huge line gives its beginning");

        // A line of a million characters, each telling its own place: where the part around the caret starts.
        StringBuilder millionText = new StringBuilder();
        for (int i = 0; i < 1000000; i++)
        {
            millionText.Append((char)('a' + (i / 1000) % 26));
        }
        EditableTextDocument million = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        million.SetText(millionText.ToString());
        string middle = AnalysisText.Get(million, 0, 600000, out int middleLine, out int middleColumn);
        Check(middleLine == 0 && middleColumn == 250000 && middle == millionText.ToString(350000, AnalysisText.BudgetChars), "a caret in the middle of a huge line is in the middle of the part (half the budget on each side)");
        string endPart = AnalysisText.Get(million, 0, 990000, out _, out int endColumn);
        Check(endColumn == 490000 && endPart == millionText.ToString(500000, AnalysisText.BudgetChars), "the part does not run past the end of the line");
    }

    private static void Main()
    {
        GoldenChecks();
        CommandLineChecks();
        EditChecks();
        TextChecks();
        AnalysisChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line editing checks passed.");
    }
}
