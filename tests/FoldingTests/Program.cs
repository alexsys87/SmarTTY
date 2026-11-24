using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using L = SmarTTY.CodeEditing.SyntaxHighlighting.Language;

// Checks of the model of the folded blocks (CodeEditing/FoldingManager.cs): which lines have a fold mark, what a fold hides, the text
// a folded line shows, the position mapping, and what expands a fold. A script folds and expands blocks of four documents (C-like with
// Allman braces, Bash keyword blocks, Python blocks by indentation, JSON) and writes down every answer of the model.
// FoldingGolden.txt is what the same script gave when the model was still one class with the painting of the fold marks and the
// decorators of the view in it (the version before this was moved to Controls/TextView/); the answers must stay the same.
// Run with: dotnet run --project tests/FoldingTests
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

    // What the script asks of the model; the label of a folded line is drawn gray by the view, which the script writes as True.
    private sealed class Model
    {
        private FoldingManager _manager;

        public void Init(TokenCache cache) { _manager = new FoldingManager(cache); }

        public string Gutter(int line) { return _manager.GetGutterState(line).ToString(); }

        public bool Collapse(int line) { return _manager.CollapseLine(line); }

        public bool ExpandLine(int line) { return _manager.ExpandLine(line); }

        public bool ExpandAt(int line, int column) { return _manager.ExpandAt(line, column); }

        public int Hidden(int line) { return _manager.GetHiddenLineCount(line); }

        public int TotalHidden() { return _manager.GetTotalHiddenLines(); }

        public int NextSpecial(int line) { return _manager.GetNextSpecialLine(line); }

        public string Transform(int line, string text)
        {
            FoldingManager.FoldedRegion region = _manager.GetRegion(line);
            if (region == null)
            {
                return "-";
            }
            string folded = region.Transform(text, out int labelStart, out int labelLength);
            return "'" + folded + "' [" + labelStart + "," + labelLength + "] True";
        }

        public string MapPosition(int line, string text, int column)
        {
            FoldingManager.FoldedRegion region = _manager.GetRegion(line);
            if (region == null)
            {
                return "-";
            }
            TextDocument.TextPosition position = region.MapPosition(text, new TextDocument.TextPosition(column, line));
            return position.Line + ":" + position.Column;
        }

        public string Collapsed() { return string.Join(";", _manager.GetCollapsedRegions().Select(x => x.Line + "=" + x.Text)); }

        public void Clear() { _manager.Clear(); }
    }

    private static EditableTextDocument Doc(string text)
    {
        EditableTextDocument document = new EditableTextDocument { DefaultLineEnding = TextDocument.LineEnding.Lf };
        document.SetText(text);
        return document;
    }

    private static readonly string[][] Docs =
    {
        new[] { "CLike", "int main() {\n    if (x)\n    {\n        a();\n    }\n\n    return 0;\n}\n\nvoid g() { b(); }\nstruct S {\n  int a;\n};\n" },
        new[] { "Bash", "if true; then\n  echo a\n  if false; then\n    echo b\n  fi\n\n  echo c\nfi\n\nwhile x; do\n  y\ndone\n" },
        new[] { "Python", "def f(x):\n    return x\n\n\nclass A:\n    def m(self):\n        pass\n\n    def n(self):\n        return 1\n\nprint(1)\n" },
        new[] { "Json", "{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": {\n    \"c\": 1\n  }\n}\n" },
    };

    private static void Dump(StringBuilder sb, Model model, EditableTextDocument doc, int lineCount, string title)
    {
        List<string> hidden = new List<string>();
        List<string> next = new List<string>();
        List<string> transforms = new List<string>();
        for (int i = 0; i < lineCount; i++)
        {
            hidden.Add(model.Hidden(i).ToString());
            next.Add(model.NextSpecial(i).ToString());
            string text = doc.GetLineText(i);
            transforms.Add(model.Transform(i, text) + "@" + model.MapPosition(i, text, 3) + "/" + model.MapPosition(i, text, 300));
        }
        sb.AppendLine(title + ": hidden=" + string.Join(",", hidden) + " total=" + model.TotalHidden() + " next=" + string.Join(",", next));
        foreach (string transform in transforms)
        {
            sb.AppendLine("  tr: " + transform);
        }
    }

    private static string RunScript()
    {
        StringBuilder sb = new StringBuilder();
        foreach (string[] item in Docs)
        {
            L language = (L)Enum.Parse(typeof(L), item[0]);
            EditableTextDocument doc = Doc(item[1]);
            TokenCache cache = new TokenCache(doc, SyntaxHighlighting.GetTokenizer(language));
            Model model = new Model();
            model.Init(cache);
            int lineCount = doc.LineCount;
            sb.AppendLine("== " + item[0] + " (" + lineCount + " lines)");
            List<string> gutter = new List<string>();
            for (int i = 0; i < lineCount; i++)
            {
                gutter.Add(model.Gutter(i));
            }
            sb.AppendLine("gutter: " + string.Join(",", gutter));
            Dump(sb, model, doc, lineCount, "plain");
            for (int i = 0; i < lineCount; i++)
            {
                bool collapsed = model.Collapse(i);
                sb.AppendLine("collapse " + i + ": " + collapsed + " total=" + model.TotalHidden() + " collapsedNow=" + model.Collapsed());
            }
            sb.AppendLine("collapse again 0: " + model.Collapse(0));
            Dump(sb, model, doc, lineCount, "all collapsed");
            List<string> gutterCollapsed = new List<string>();
            for (int i = 0; i < lineCount; i++)
            {
                gutterCollapsed.Add(model.Gutter(i));
            }
            sb.AppendLine("gutter collapsed: " + string.Join(",", gutterCollapsed));
            for (int i = 0; i < lineCount; i++)
            {
                for (int column = -1; column <= 6; column += (column == -1 ? 1 : 3))
                {
                    Model other = new Model();
                    other.Init(cache);
                    for (int k = 0; k < lineCount; k++)
                    {
                        other.Collapse(k);
                    }
                    bool expanded = other.ExpandAt(i, column);
                    sb.AppendLine("expandAt " + i + "," + column + ": " + expanded + " total=" + other.TotalHidden() + " " + other.Collapsed());
                }
            }
            for (int i = 0; i < lineCount; i++)
            {
                bool expanded = model.ExpandLine(i);
                sb.AppendLine("expandLine " + i + ": " + expanded + " total=" + model.TotalHidden());
            }
            sb.AppendLine("collapse 0 then clear");
            model.Collapse(0);
            model.Clear();
            sb.AppendLine("after clear total=" + model.TotalHidden() + " " + model.Collapsed());
        }
        return sb.ToString();
    }

    // The blocks that were folded before the text was loaded again: each goes back to its line when the line has its text, or to the one line
    // that has its text (this is the part of the editor's RestoreFoldState that needs no control).
    private static string RestoredFolds(string text, SyntaxHighlighting.Language language, params FoldedLineInfo[] items)
    {
        EditableTextDocument doc = Doc(text);
        TokenCache cache = new TokenCache(doc, SyntaxHighlighting.GetTokenizer(language));
        FoldingManager manager = new FoldingManager(cache);
        manager.RestoreFolds(items);
        return string.Join(";", manager.GetCollapsedRegions().Select(x => x.Line + "=" + x.Text)) + " hidden=" + manager.GetTotalHiddenLines();
    }

    private static FoldedLineInfo Info(int line, string text)
    {
        return new FoldedLineInfo { Line = line, Text = text };
    }

    private static void RestoreChecks()
    {
        string python = "def f(x):\n    return x\n\n\nclass A:\n    def m(self):\n        pass\n\n    def n(self):\n        return 1\n\nprint(1)\n";
        L py = L.Python;
        Equal(RestoredFolds(python, py, Info(0, "def f(x):"), Info(4, "class A:")), "0=def f(x):;4=class A: hidden=6", "folds go back to the lines that still have their text");
        Equal(RestoredFolds(python, py, Info(2, "class A:")), "4=class A: hidden=5", "a line that moved is found by its text when it is the only one with it");
        Equal(RestoredFolds(python, py, Info(99, "def f(x):")), "0=def f(x): hidden=1", "a line past the end of the text is found by its text");
        Equal(RestoredFolds(python, py, Info(0, "no such line")), " hidden=0", "a text that is nowhere in the document folds nothing");
        Equal(RestoredFolds(python, py, Info(1, "")), " hidden=0", "an empty text has several lines (and a line that does not fold): nothing is folded");
        Equal(RestoredFolds(python, py), " hidden=0", "no blocks, no folds");

        string twice = "int a() {\n  x;\n}\nint a() {\n  y;\n}\nint b() {\n  z;\n}\n";
        Equal(RestoredFolds(twice, L.CLike, Info(5, "int a() {")), " hidden=0", "a text that two lines have, asked for at a line that does not have it, folds nothing");
        Equal(RestoredFolds(twice, L.CLike, Info(3, "int a() {")), "3=int a() { hidden=2", "a text that two lines have is folded at the line that was asked for, when it has the text");
        Equal(RestoredFolds(twice, L.CLike, Info(1, "int b() {")), "6=int b() { hidden=2", "a line with another text is found by its own text");
    }

    // FoldingManager.ClickMark: what a click on the mark of the fold margin does (3D-4, moved from FoldingMarginPainter.OnMouseDown).
    private static void ClickChecks()
    {
        EditableTextDocument doc = Doc(Docs[0][1]);
        FoldingManager manager = new FoldingManager(new TokenCache(doc, SyntaxHighlighting.GetTokenizer(L.CLike)));
        string Click(int line, bool wrapping, int caretLine)
        {
            bool changed = manager.ClickMark(line, wrapping, new TextDocument.TextPosition(2, caretLine), out TextDocument.TextPosition? target);
            return changed + " " + (target.HasValue ? target.Value.Line + ":" + target.Value.Column : "-") + " hidden=" + manager.GetTotalHiddenLines();
        }
        Equal(Click(0, true, 3), "False - hidden=0", "while the lines are wrapped a click folds nothing");
        Equal(Click(0, false, 3), "True -1:0 hidden=7", "a block whose brace ends its header line: the caret inside goes to the line before that line (-1 here)");
        Equal(Click(0, false, 3), "True - hidden=0", "a click on a folded line unfolds it, the caret stays");
        Equal(Click(1, false, 3), "True 1:0 hidden=3", "a block whose brace is on the next line: the caret inside goes to the start of the header");
        Equal(Click(1, false, 3), "True - hidden=0", "unfolded");
        Equal(Click(1, false, 1), "True - hidden=3", "the caret on the header, before the line of the brace, stays");
        Click(1, false, 1);
        Equal(Click(1, false, 2), "True 1:0 hidden=3", "the caret on the line of the opening brace moves");
        Click(1, false, 1);
        Equal(Click(1, false, 4), "True 1:0 hidden=3", "and on the line of the closing brace");
        Click(1, false, 1);
        Equal(Click(1, false, 5), "True - hidden=3", "the caret after the block stays");
        Click(1, false, 1);
        Equal(Click(5, false, 3), "False - hidden=0", "a blank line has no block");
        Equal(Click(99, false, 3), "False - hidden=0", "nor a line past the text");
        ITextFolds folds = manager;
        Click(1, false, 1);
        string folded = folds.GetFoldedText(1, doc.GetLineText(1), out int labelStart, out int labelLength);
        Equal(folded + " [" + labelStart + "," + labelLength + "] " + folds.GetHiddenLineCount(1), "    if (x) {...} [12,3] 3", "the text of a folded header as the WPF view asks for it");
        Equal((folds.GetFoldedText(0, doc.GetLineText(0), out _, out _) ?? "null") + " " + folds.GetHiddenLineCount(0), "null 0", "a line that is not folded has none");
    }

    private static void Main()
    {
        RestoreChecks();
        ClickChecks();
        string expected = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FoldingGolden.txt")).Replace("\r\n", "\n");
        string actual = RunScript().Replace("\r\n", "\n");
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            if (expectedLines[i] != actualLines[i])
            {
                throw new Exception("FAIL: line " + (i + 1) + " of the answers of the folding model differs\n  expected: " + expectedLines[i] + "\n  actual:   " + actualLines[i]);
            }
        }
        Check(expectedLines.Length == actualLines.Length, "the folding model gives " + expectedLines.Length + " lines of answers, as before");
        Check(actual == expected, "every answer of the folding model is the same as before it was separated from the painting");

        // The golden file must not be an empty script: it has real folds in every document.
        Check(expectedLines.Count(l => l.StartsWith("  tr: '")) >= 14, "the script folds at least 14 header lines and reads their text");
        Check(expected.Contains("'    if (x) {...}' [12,3]"), "a brace on its own line (Allman style) folds from the line above: the label is after the condition");
        Check(expected.Contains("'if true; then...fi' [13,3]") && expected.Contains("'class A:...' [8,3]") && expected.Contains("'  \"b\": {...}' [8,3]"), "keyword, indentation and JSON blocks fold with their labels");
        Check(expected.Contains("gutter: Expandable") && expected.Contains("SectionEnd") && expected.Contains("Collapsed"), "the fold marks cover the expandable, collapsed and section end lines");
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " folding model checks passed.");
    }
}
