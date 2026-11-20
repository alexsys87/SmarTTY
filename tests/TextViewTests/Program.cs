using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using K = System.Windows.Forms.Keys;

// Drives the real text view (Controls/ReadonlyTextBrowser.cs, through a TextEditorControl shown in a form) and writes down where its caret goes,
// so that the moves of the caret and the wrap map can be taken out of the control (stage 3C) without changing what they do:
//   - for every position of a document of 30 lines (empty lines, tabs, words with punctuation, letters that are not ASCII, blanks, a line longer
//     than the wrap width) the target of each navigation key (Left, Right, Up, Down, Home, End, PageUp, PageDown, as the key handler finds it),
//     of the word moves and of Ctrl+Home and Ctrl+End (asked of ComputeMovedPosition with the keys, because a test cannot hold Ctrl), and the
//     word under the caret (TryGetWordBounds);
//   - runs of keys through the real key handler, where Up, Down and the pages keep the column the caret came from;
//   - the same with word wrap at 20 columns, and the wrap map (the breaks of every line, the line and the row of every row index, the row index
//     of positions).
// The view is sized until it shows exactly 8 rows (and wraps at 20 columns), so that the lines do not depend on the fonts.
// TextViewGolden.txt is what the code wrote before the move; it is never written again. Since the move (3C-1) the accessors read the kept column
// and the wrap map of the model (Navigator, WrapLayout) instead of the fields of the view.
//   xvfb-run -a mono tests/TextViewTests/bin/Release/net48/TextViewTests.exe            compares with the golden file
//   ... TextViewTests.exe --record <file>                                                writes the golden file (old code only)
internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly Type View = typeof(ReadonlyTextBrowser);

    private static readonly List<string> Lines = new List<string>();

    private static TextEditorControl _editor;

    private static Form _form;

    private static readonly string[] DocumentLines = BuildLines();

    private static string[] BuildLines()
    {
        List<string> lines = new List<string>
        {
            "",
            "abc def_ghi  jkl",
            "\tindent\tx",
            "  two spaces",
            "foo.bar(baz, 42);",
            "the quick brown fox jumps over the lazy dog ok",
            "x",
            "été ünï",
            "    ",
            "end",
            "a\tb\tc\td"
        };
        for (int i = lines.Count; i < 30; i++)
        {
            lines.Add("line " + i.ToString(CultureInfo.InvariantCulture) + new string('.', (i * 7) % 23));
        }
        return lines.ToArray();
    }

    private static T Field<T>(string name)
    {
        return (T)View.GetField(name, Private).GetValue(_editor);
    }

    private static void SetField(string name, object value)
    {
        View.GetField(name, Private).SetValue(_editor, value);
    }

    private static object Call(string name, params object[] arguments)
    {
        return View.GetMethod(name, Private).Invoke(_editor, arguments);
    }

    private static T Property<T>(string name)
    {
        return (T)View.GetProperty(name, Private).GetValue(_editor, null);
    }

    private static string P(TextDocument.TextPosition position)
    {
        return position.Line.ToString(CultureInfo.InvariantCulture) + ":" + position.Column.ToString(CultureInfo.InvariantCulture);
    }

    private static TextDocument.TextPosition At(int line, int column)
    {
        return new TextDocument.TextPosition(column, line);
    }

    // The target of a navigation key from a position, as the key handler finds it (the column to keep is taken anew).
    private static string Target(TextDocument.TextPosition from, Keys key)
    {
        _editor.SetCaret(from);
        _editor.Navigator.DesiredColumn = -1;
        object[] arguments = { new KeyEventArgs(key), false, null, null };
        bool found = (bool)View.GetMethod("TryGetNavigationTarget", Private).Invoke(_editor, arguments);
        return found ? P((TextDocument.TextPosition)arguments[2]) : "-";
    }

    // CaretMove was a protected enum of the view (CodeEditing/CaretNavigator since 3C).
    private static string Moved(TextDocument.TextPosition from, string move, Keys keys)
    {
        _editor.SetCaret(from);
        _editor.Navigator.DesiredColumn = -1;
        object caretMove = Enum.Parse(typeof(SmarTTY.CodeEditing.CaretMove), move);
        try
        {
            return P((TextDocument.TextPosition)View.GetMethod("ComputeMovedPosition", Private).Invoke(_editor, new object[] { caretMove, keys, from, false }));
        }
        catch (TargetInvocationException exception)
        {
            // Recorded as it is: a fault of the old code is kept by the move.
            return "throws:" + exception.InnerException.GetType().Name;
        }
    }

    private static string Word(TextDocument.TextPosition at)
    {
        object[] arguments = { at, null, null };
        bool found = (bool)View.GetMethod("TryGetWordBounds", Private).Invoke(_editor, arguments);
        return found ? P((TextDocument.TextPosition)arguments[1]) + "-" + P((TextDocument.TextPosition)arguments[2]) : "none";
    }

    private static void Moves(string title)
    {
        Lines.Add("== moves " + title);
        for (int line = 0; line < DocumentLines.Length; line++)
        {
            for (int column = 0; column <= DocumentLines[line].Length; column++)
            {
                TextDocument.TextPosition from = At(line, column);
                Lines.Add(P(from)
                    + " L=" + Target(from, Keys.Left) + " R=" + Target(from, Keys.Right)
                    + " U=" + Target(from, Keys.Up) + " D=" + Target(from, Keys.Down)
                    + " H=" + Target(from, Keys.Home) + " E=" + Target(from, Keys.End)
                    + " PU=" + Target(from, Keys.Prior) + " PD=" + Target(from, Keys.Next)
                    + " WL=" + Moved(from, "WordLeft", Keys.None) + " WR=" + Moved(from, "WordRight", Keys.None)
                    + " CH=" + Moved(from, "Home", Keys.Control) + " CE=" + Moved(from, "End", Keys.Control)
                    + " W=" + Word(from));
            }
        }
        Lines.Add("other key: " + Target(At(3, 2), K.A) + ", outside the text: " + Moved(At(-1, 0), "Left", Keys.None));
    }

    private static void Press(string title, TextDocument.TextPosition start, params Keys[] keys)
    {
        _editor.SetCaret(start);
        _editor.Navigator.DesiredColumn = -1;
        List<string> steps = new List<string>();
        MethodInfo keyDown = View.GetMethod("OnKeyDown", Private);
        foreach (Keys key in keys)
        {
            keyDown.Invoke(_editor, new object[] { new KeyEventArgs(key) });
            steps.Add(key + ">" + P(_editor.CaretPosition) + "@" + Property<int>("ScrollLine").ToString(CultureInfo.InvariantCulture));
        }
        Lines.Add("keys " + title + " from " + P(start) + ": " + string.Join(" ", steps));
    }

    private static void Runs(string title)
    {
        Lines.Add("== runs " + title);
        Press("down through short lines", At(5, 30), K.Down, K.Down, K.Down, K.Down, K.Down, K.Up, K.Up, K.Up);
        Press("a column kept across tabs", At(1, 9), K.Down, K.Down, K.Up, K.Up);
        Press("right forgets the column", At(5, 30), K.Down, K.Right, K.Up);
        Press("pages", At(2, 3), K.Next, K.Next, K.Next, K.Next, K.Prior, K.Prior, K.Prior, K.Prior, K.Prior);
        Press("down to the end and past it", At(25, 2), K.Down, K.Down, K.Down, K.Down, K.Down, K.Down);
        Press("up to the start and past it", At(2, 4), K.Up, K.Up, K.Up, K.Up);
        Press("home and end", At(3, 6), K.Home, K.Home, K.Home, K.End, K.Left, K.Right, K.Right, K.Left);
        Press("left and right across lines", At(6, 0), K.Left, K.Left, K.Right, K.Right, K.Right);
        Press("a line off the screen", At(20, 3), K.Up, K.Down, K.Down);
    }

    private static void WrapMap()
    {
        Lines.Add("== wrap map");
        int[][] breaks = _editor.WrapLayout.Breaks;
        int[] firstRow = _editor.WrapLayout.FirstRows;
        for (int line = 0; line < DocumentLines.Length; line++)
        {
            Lines.Add("line " + line + " first row " + firstRow[line] + " breaks " + (breaks[line] == null ? "none" : string.Join(",", Array.ConvertAll(breaks[line], b => b.ToString(CultureInfo.InvariantCulture)))));
        }
        int rows = Property<int>("ScrollRowCount");
        Lines.Add("rows " + rows + ", last entry " + firstRow[DocumentLines.Length]);
        List<string> rowLines = new List<string>();
        for (int row = -1; row <= rows + 1; row++)
        {
            object[] arguments = { row, null };
            int line = (int)View.GetMethod("RowIndexToLine", Private).Invoke(_editor, arguments);
            rowLines.Add(row + ">" + line + "." + arguments[1]);
        }
        Lines.Add("row index to line: " + string.Join(" ", rowLines));
        List<string> rowIndexes = new List<string>();
        foreach (int line in new[] { -3, 0, 1, 2, 5, 10, 29, 40 })
        {
            foreach (int column in new[] { -1, 0, 7, 19, 20, 21, 39, 40, 45, 100 })
            {
                rowIndexes.Add(line + ":" + column + ">" + Call("PositionToRowIndex", line, column) + "/" + Call("WrapSegmentOf", line, column));
            }
        }
        Lines.Add("position to row index / segment: " + string.Join(" ", rowIndexes));
        List<string> segments = new List<string>();
        foreach (int line in new[] { 0, 2, 5, 10 })
        {
            for (int segment = 0; segment < 4; segment++)
            {
                int end = (breaks[line] == null || segment >= breaks[line].Length) ? int.MaxValue : breaks[line][segment];
                if (breaks[line] != null && segment > breaks[line].Length)
                {
                    break;
                }
                segments.Add(line + "." + segment + ">" + Call("WrapSegmentStart", line, segment) + "-" + (end == int.MaxValue ? "max" : ((int)Call("WrapSegmentEnd", line, segment)).ToString(CultureInfo.InvariantCulture)));
            }
        }
        Lines.Add("segments: " + string.Join(" ", segments));
    }

    private static void SizeTo(int rows, int wrapWidth)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            Application.DoEvents();
            int visibleRows = Field<int>("_visibleRows");
            int width = Field<int>("_wrapWidth");
            bool rowsDone = visibleRows == rows;
            bool widthDone = wrapWidth == 0 || width == wrapWidth;
            if (rowsDone && widthDone)
            {
                return;
            }
            System.Drawing.Size size = _form.ClientSize;
            int height = size.Height + (rowsDone ? 0 : (visibleRows < rows ? 1 : -1));
            int formWidth = size.Width + (widthDone ? 0 : (width < wrapWidth ? 1 : -1));
            _form.ClientSize = new System.Drawing.Size(formWidth, height);
        }
        throw new InvalidOperationException("the view could not be sized to " + rows + " rows and " + wrapWidth + " columns: " + Field<int>("_visibleRows") + ", " + Field<int>("_wrapWidth"));
    }

    private static void Run()
    {
        _form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(0, 0), ClientSize = new System.Drawing.Size(900, 150) };
        _editor = new TextEditorControl { Dock = DockStyle.Fill };
        _form.Controls.Add(_editor);
        _editor.SetDocument(new EditableTextDocument(DocumentLines, TextDocument.LineEnding.Lf));
        _editor.TabSize = 4;
        _form.Show();
        SizeTo(8, 0);
        Lines.Add("rows " + Field<int>("_visibleRows") + ", wrapping " + Bool(_editor.IsWrapping));
        Moves("without wrap");
        Runs("without wrap");

        _editor.WordWrap = true;
        _form.ClientSize = new System.Drawing.Size(300, _form.ClientSize.Height);
        SizeTo(8, 20);
        Lines.Add("rows " + Field<int>("_visibleRows") + ", wrapping " + Bool(_editor.IsWrapping) + " at " + Field<int>("_wrapWidth"));
        WrapMap();
        Moves("with wrap");
        Runs("with wrap");
        Press("down through the rows of a wrapped line", At(4, 15), K.Down, K.Down, K.Down, K.Down, K.Down, K.Up, K.Up, K.Up, K.Up);
        Press("the end of a row", At(5, 19), K.Down, K.Up, K.End, K.Home, K.Down);

        // The tab size changes the breaks; a text that changes too.
        _editor.TabSize = 8;
        WrapMap();
        _editor.SetDocument(new EditableTextDocument(new[] { "short", new string('w', 61), "\t\t\t\t\tafter tabs that pass the width" }, TextDocument.LineEnding.Lf));
        Application.DoEvents();
        WrapMap2(3);
        _form.Dispose();
    }

    // The wrap map of a document of a few lines.
    private static void WrapMap2(int lineCount)
    {
        int[][] breaks = _editor.WrapLayout.Breaks;
        Call("EnsureWrapMap");
        breaks = _editor.WrapLayout.Breaks;
        int[] firstRow = _editor.WrapLayout.FirstRows;
        List<string> parts = new List<string>();
        for (int line = 0; line < lineCount; line++)
        {
            parts.Add(line + ":" + firstRow[line] + "[" + (breaks[line] == null ? "" : string.Join(",", Array.ConvertAll(breaks[line], b => b.ToString(CultureInfo.InvariantCulture)))) + "]");
        }
        Lines.Add("== another text: " + string.Join(" ", parts) + " rows " + firstRow[lineCount]);
    }

    private static string Bool(bool value)
    {
        return value ? "T" : "F";
    }

    private static int Main(string[] args)
    {
        try
        {
            Run();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return 1;
        }
        string text = string.Join("\n", Lines) + "\n";
        if (args.Length == 2 && args[0] == "--record")
        {
            File.WriteAllText(args[1], text, new UTF8Encoding(false));
            Console.WriteLine("Recorded " + Lines.Count + " lines in " + args[1]);
            return 0;
        }
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TextViewGolden.txt");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        string[] actual = text.TrimEnd('\n').Split('\n');
        int mismatches = 0;
        for (int i = 0; i < Math.Max(expected.Length, actual.Length); i++)
        {
            string expectedLine = i < expected.Length ? expected[i] : "(none)";
            string actualLine = i < actual.Length ? actual[i] : "(none)";
            if (expectedLine != actualLine)
            {
                if (mismatches < 10)
                {
                    Console.WriteLine("FAIL: line " + (i + 1));
                    Console.WriteLine("  expected: " + expectedLine);
                    Console.WriteLine("  actual:   " + actualLine);
                }
                mismatches++;
            }
        }
        if (mismatches > 0)
        {
            Console.WriteLine("FAIL: " + mismatches + " of " + actual.Length + " lines differ from the golden file");
            return 1;
        }
        Console.WriteLine("PASS: all " + actual.Length + " lines of the text view match the golden file");
        Console.WriteLine();
        Console.WriteLine("All text view checks passed.");
        return 0;
    }
}
