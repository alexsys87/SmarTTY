using SmarTTY.CodeEditing;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// What a case of tests/TerminalSnapshots set on the WinForms terminal, on the display model of the WPF terminal (the settings a case changes
/// have the names they had on the control). The two that the model has no use for are kept to say so: <see cref="EnterSequence"/> and
/// <see cref="InputEncoding"/> change only what keys send, which the probes of the old stand wrote and this one does not.
/// </summary>
internal sealed class SnapshotTerminal
{
    public readonly TerminalDisplayModel Model = new TerminalDisplayModel(80, 24);

    public int MaxChunkLength
    {
        set => Model.MaxChunkLength = value;
    }

    public bool ShowUnrecognizedAsHex
    {
        set => Model.ShowUnrecognizedAsHex = value;
    }

    public bool SuppressReplies
    {
        set => Model.SuppressReplies = value;
    }

    public int TabWidth
    {
        set => Model.TabWidth = value;
    }

    public bool HighlightLogLevels
    {
        set => Model.HighlightLogLevels = value;
    }

    public bool UrlLinks
    {
        set => Model.UrlLinks = value;
    }

    public bool FileLinks
    {
        set => Model.FileLinks = value;
    }

    public CellStyle CurrentStyle
    {
        set => Model.CurrentStyle = value;
    }

    /// <summary>The directory a relative file link is put under when the shell reported none (see <see cref="LinkedPath"/>).</summary>
    public Func<string> DirectoryFallback;

    /// <summary>The WinForms terminal tracked the style of every line with it; the display model always does what it did without it.</summary>
    public bool AlwaysTrackLines;

    public string EnterSequence;

    public Encoding InputEncoding;
}

// Runs the cases of the stand of the WinForms terminal on TerminalDisplayModel and compares what it writes down with the goldens that stand
// wrote on the control before the control was removed (stage 2): the state of the screen after the output (TerminalModelGolden.txt: the
// lines with the style of every cell, the cursor, the title, the modes, the bells, the replies, the exceptions, the file links a click opens),
// the colours the WPF view paints every cell with (TerminalCellColorsGolden.txt, from RowRunBuilder and TerminalColorScheme), and the bytes of
// the mouse reports (TerminalMouseGolden.txt, from TerminalInputEncoder as the view calls it). The keys and the pastes, which the old stand also
// probed, were the WinForms input; the keys of the WPF view are checked by TerminalKeyEncoderTests. The goldens are never recorded again.
internal static partial class Program
{
    private const string Esc = "\u001b";

    private const string Csi = "\u001b[";

    private const string Bel = "\a";

    private const int Columns = 80;

    private const int Rows = 24;

    private sealed class Case
    {
        public string Name;

        // The chunks of output, in the order they arrive.
        public string[] Chunks;

        public Action<SnapshotTerminal> Setup;

        // Whether the output is also fed one character at a time: the screen must come out the same.
        public bool AlsoByChar;

        // The old stand tried keys and pastes after the output; here it only decides nothing.
        public bool Probe = true;

        // The case is in the file of the painted colours only.
        public bool SpansOnly;
    }

    private sealed class Harness : IOutputLinkHost
    {
        public SnapshotTerminal Terminal;

        public readonly StringBuilder Sent = new StringBuilder();

        public int Bells;

        public int TitleChanges;

        public readonly List<string> Exceptions = new List<string>();

        public readonly List<string> Clicks = new List<string>();

        // The old stand gave the terminal no settings, so no list of extensions that make a bare name a link.
        public bool IsLinkExtension(string extension)
        {
            return false;
        }

        public void RequestOpenFile(string path, int? line, int? column, ColumnUnit unit)
        {
            Clicks.Add(Escape(LinkedPath.Resolve(path, Terminal.DirectoryFallback)) + ":" + line + ":" + column + ":" + unit);
        }

        public void RequestOpenUrl(string url)
        {
        }
    }

    private static readonly List<string> StateLines = new List<string>();

    private static readonly List<string> CellLines = new List<string>();

    private static int Main()
    {
        foreach (Case testCase in HandWrittenCases())
        {
            RunCase(testCase);
        }
        for (int seed = 1; seed <= 6; seed++)
        {
            foreach (Case testCase in RandomCases(seed, 60))
            {
                Harness harness = Run(testCase, byChar: false, out string state);
                CellLines.Add(testCase.Name + " " + Hash(DumpCells(harness)));
                string[] stateLines = state.Split('\n');
                StateLines.Add(testCase.Name + " " + stateLines[0] + " lines " + stateLines.Count(line => line.Length > 3 && char.IsDigit(line[0]) && line[3] == '|') + " " + Hash(state));
            }
        }
        int failed = Compare("terminal state", "TerminalModelGolden.txt", StateLines, KnownDifferences);
        failed += Compare("painted colours", "TerminalCellColorsGolden.txt", CellLines);
        failed += Compare("mouse reports", "TerminalMouseGolden.txt", RunMouse());
        return failed == 0 ? 0 : 1;
    }

    private static void RunCase(Case testCase)
    {
        Harness harness = Run(testCase, byChar: false, out string whole);
        CellLines.Add("=== " + testCase.Name);
        CellLines.AddRange(DumpCells(harness).Split('\n'));
        if (testCase.SpansOnly)
        {
            return;
        }
        StateLines.Add("=== " + testCase.Name);
        StateLines.AddRange(whole.Split('\n'));
        if (testCase.AlsoByChar)
        {
            Run(testCase, byChar: true, out string perChar);
            StateLines.Add(perChar == whole ? "by character: same" : "by character: DIFFERENT " + Hash(perChar));
        }
    }

    private static Harness Run(Case testCase, bool byChar, out string state)
    {
        Harness harness = new Harness { Terminal = new SnapshotTerminal() };
        TerminalDisplayModel model = harness.Terminal.Model;
        model.LinkHost = harness;
        model.Reply += reply => harness.Sent.Append("KeySequence:").Append(Escape(reply)).Append(' ');
        model.Bell += () => harness.Bells++;
        model.TitleChanged += () => harness.TitleChanges++;
        testCase.Setup?.Invoke(harness.Terminal);
        int chunkIndex = 0;
        foreach (string chunk in testCase.Chunks)
        {
            if (byChar)
            {
                foreach (char ch in chunk)
                {
                    Feed(harness, ch.ToString(), chunkIndex++);
                }
            }
            else
            {
                Feed(harness, chunk, chunkIndex++);
            }
        }
        StringBuilder text = new StringBuilder();
        Dump(harness, text);
        state = text.ToString().TrimEnd('\n');
        return harness;
    }

    private static void Feed(Harness harness, string chunk, int index)
    {
        try
        {
            harness.Terminal.Model.ProcessOutput(chunk);
        }
        catch (Exception exception)
        {
            harness.Exceptions.Add(index + ":" + exception.GetType().Name);
        }
    }

    // ---- what is written down (the format of the old stand) -------------------------------------------------------------

    private static void Dump(Harness harness, StringBuilder text)
    {
        TerminalDisplayModel model = harness.Terminal.Model;
        text.Append("size ").Append(model.Columns).Append('x').Append(model.Rows)
            .Append(" buffer ").Append(model.ActiveBufferIndex)
            .Append(" cursor ").Append(model.CursorPosition.X).Append(',').Append(model.CursorPosition.Y)
            .Append(" title ").Append(model.Title == null ? "<null>" : "\"" + Escape(model.Title) + "\"")
            .Append(" mouse ").Append(model.MouseMode)
            .Append(" bells ").Append(harness.Bells)
            .Append(" titleEvents ").Append(harness.TitleChanges)
            // The model is never resized here; the old stand subscribed after the form had its size.
            .Append(" sizeEvents ").Append(0)
            .Append(" directory ").Append(model.ReportedDirectory == null ? "<null>" : "\"" + Escape(model.ReportedDirectory) + "\"")
            .Append('\n');
        text.Append("style ").Append(DescribeStyle(model.CurrentStyle)).Append('\n');
        text.Append("sent ").Append(harness.Sent).Append('\n');
        text.Append("threw ").Append(string.Join(" ", harness.Exceptions)).Append('\n');
        EditableTextDocument document = model.Document;
        for (int line = 0; line < document.LineCount; line++)
        {
            text.Append(line.ToString("D3")).Append('|').Append(Escape(document.GetLineText(line))).Append('|');
            LineInfo info = model.Lines.GetLine(line);
            text.Append(info == null ? "no info" : DescribeLine(info));
            text.Append('\n');
        }
        // Clicking every file link: what the terminal asks to open.
        harness.Clicks.Clear();
        HashSet<object> clicked = new HashSet<object>();
        for (int line = 0; line < document.LineCount; line++)
        {
            CellStyle[] cells = model.Lines.GetLine(line)?.Cells;
            if (cells == null)
            {
                continue;
            }
            foreach (CellStyle cell in cells)
            {
                ILinkTarget target = cell._link.Target;
                if (target != null && target.GetType().Name == "LinkCandidate" && target.IsActive && clicked.Add(target))
                {
                    target.Activate();
                }
            }
        }
        text.Append("clicks ").Append(string.Join(" ", harness.Clicks)).Append('\n');
    }

    private static string DescribeLine(LineInfo info)
    {
        CellStyle[] cells = info.Cells;
        StringBuilder text = new StringBuilder();
        text.Append(info.IsDimmed ? "dim " : "").Append(info.IsWrapped ? "wrap " : "");
        if (cells == null)
        {
            return text.Append("no cells").ToString();
        }
        int start = 0;
        for (int column = 1; column <= cells.Length; column++)
        {
            if (column == cells.Length || !cells[column].StyleEquals(cells[start]))
            {
                if (!cells[start].IsDefault || cells[start].Override != null)
                {
                    text.Append(start).Append('+').Append(column - start).Append('[').Append(DescribeStyle(cells[start])).Append("] ");
                }
                start = column;
            }
        }
        text.Append("of ").Append(cells.Length);
        return text.ToString();
    }

    // The text a link target holds: the file name of a file link, the address of an address link.
    private static string DescribeTarget(object target)
    {
        foreach (string name in new[] { "PathText", "Url" })
        {
            System.Reflection.PropertyInfo property = target.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (property != null)
            {
                return "=" + Escape(Convert.ToString(property.GetValue(target)));
            }
        }
        return "";
    }

    private static string DescribeStyle(CellStyle style)
    {
        StringBuilder text = new StringBuilder();
        text.Append("fg").Append(style.Foreground).Append(" bg").Append(style.Background);
        if (style.Bold)
        {
            text.Append(" bold");
        }
        if (style.Inverse)
        {
            text.Append(" inverse");
        }
        if (style.Underline)
        {
            text.Append(" underline");
        }
        if (style.ExtendedForeground != 0)
        {
            text.Append(" efg").Append(style.ExtendedForeground.ToString("X"));
        }
        if (style.ExtendedBackground != 0)
        {
            text.Append(" ebg").Append(style.ExtendedBackground.ToString("X"));
        }
        if (style.Override != null)
        {
            text.Append(" override");
        }
        if (style._link.Severity != 0)
        {
            text.Append(" severity").Append(style._link.Severity);
        }
        if (style._link.Target != null)
        {
            text.Append(" link:").Append(style._link.Target.GetType().Name).Append(style._link.Target.IsActive ? "(active)" : "(inactive)").Append(DescribeTarget(style._link.Target));
        }
        return text.ToString();
    }

    // ---- the painted colours ----------------------------------------------------------------------------------------------

    // The colours of every painted cell as the WPF view paints them (RowRunBuilder with the colour rule of the terminal): the background, and
    // the text colour and the underline where they show. The same format as the old stand made from the colour spans of the WinForms control.
    private static string DumpCells(Harness harness)
    {
        TerminalDisplayModel model = harness.Terminal.Model;
        TerminalColorScheme scheme = new TerminalColorScheme();
        RowLayout layout = new RowLayout();
        StringBuilder text = new StringBuilder();
        for (int line = 0; line < model.Document.LineCount; line++)
        {
            string lineText = model.Document.GetLineText(line);
            RowRunBuilder.Build(lineText, model.Lines.GetLine(line), scheme, Columns, layout);
            int length = Math.Min(lineText.Length, Columns);
            List<string> cells = new List<string>();
            for (int column = 0; column < length; column++)
            {
                string background = "-";
                foreach (BackgroundRun run in layout.Backgrounds)
                {
                    if (run.Start <= column && column < run.Start + run.Length)
                    {
                        background = Hex(run.Color);
                    }
                }
                string foreground = Hex(scheme.DefaultForeground);
                bool underline = false;
                foreach (TextRun run in layout.Texts)
                {
                    if (run.Start <= column && column < run.Start + run.Length)
                    {
                        foreground = Hex(run.Foreground);
                        underline = run.Underline;
                    }
                }
                cells.Add(CellDescription(lineText[column], background, foreground, underline));
            }
            text.Append(line.ToString("D3")).Append('|').Append(CellRuns(cells)).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string Hex(RgbColor color)
    {
        return "#" + color.Argb.ToString("X8");
    }

    // One cell as the cell colours golden writes it (the same function is in the old stand that wrote it).
    private static string CellDescription(char ch, string background, string foreground, bool underline)
    {
        if (foreground == "#FFD3D3D3")
        {
            foreground = "default";
        }
        bool blank = ch == ' ' || ch == '\0';
        return "bg=" + background + (blank && !underline ? "" : " fg=" + foreground) + (underline ? " u" : "");
    }

    private static string CellRuns(List<string> cells)
    {
        StringBuilder text = new StringBuilder();
        int start = 0;
        for (int column = 1; column <= cells.Count; column++)
        {
            if (column == cells.Count || cells[column] != cells[start])
            {
                text.Append(start).Append('-').Append(column).Append(' ').Append(cells[start]).Append("; ");
                start = column;
            }
        }
        return text.ToString();
    }

    // ---- the mouse --------------------------------------------------------------------------------------------------------

    // The steps of the mouse golden as the WPF view turns its events into calls of the encoder (TerminalRenderView.OnMouseDown, OnMouseUp,
    // OnMouseMove, OnMouseWheel): the button of a press (a button the terminal does not report is Other), the buttons held during a move, the
    // direction of the wheel (Delta <= 0 is down), the cell under the mouse. A press outside the text area is no event of the view at all.
    // The steps of "cell 0,0" are outside it too: the old stand looked for the first pixel the control put in that cell and clicked two
    // pixels further, which was in the padding at the left of the text, where the control reports nothing (its TextArea test).
    private static List<string> RunMouse()
    {
        List<string> lines = new List<string>();
        (string Name, int Mode)[] modes = { ("off", 0), ("x10", 9), ("normal", 1000), ("highlight", 1001), ("button-event", 1002), ("any-event", 1003) };
        (string Name, TerminalMouseButton Button)[] buttons =
        {
            ("Left", TerminalMouseButton.Left), ("Middle", TerminalMouseButton.Middle), ("Right", TerminalMouseButton.Right), ("XButton1", TerminalMouseButton.Other)
        };
        (string Name, TerminalMouseButton Button)[] held =
        {
            ("None", TerminalMouseButton.None), ("Left", TerminalMouseButton.Left), ("Middle", TerminalMouseButton.Middle), ("Right", TerminalMouseButton.Right)
        };
        foreach (var mode in modes)
        {
            foreach (bool sgr in new[] { false, true })
            {
                TerminalDisplayModel model = new TerminalDisplayModel(Columns, Rows);
                TerminalInputEncoder mouse = new TerminalInputEncoder();
                model.MouseModeChanged += mouse.ResetMouseMotion;
                StringBuilder fill = new StringBuilder();
                for (int row = 1; row <= Rows; row++)
                {
                    fill.Append(Csi).Append(row).Append(";1H").Append(new string('x', Columns));
                }
                model.ProcessOutput(fill + Csi + "H" + (mode.Mode == 0 ? "" : Csi + "?" + mode.Mode + "h") + (sgr ? Csi + "?1006h" : ""));
                foreach (GridPoint cell in new[] { new GridPoint(0, 0), new GridPoint(10, 5), new GridPoint(Columns - 1, Rows - 1), new GridPoint(1, 1) })
                {
                    string head = mode.Name + (sgr ? " sgr" : "") + " cell " + cell.X + "," + cell.Y;
                    if (cell.X == 0 && cell.Y == 0)
                    {
                        foreach (var button in buttons)
                        {
                            lines.Add(head + " down " + button.Name + " -> ");
                            lines.Add(head + " up " + button.Name + " -> ");
                        }
                        foreach (int delta in new[] { 120, -120, 0 })
                        {
                            lines.Add(head + " wheel " + delta + " -> ");
                        }
                        foreach (var button in held)
                        {
                            lines.Add(head + " move " + button.Name + " #0 -> ");
                            lines.Add(head + " move " + button.Name + " #1 -> ");
                        }
                        continue;
                    }
                    foreach (var button in buttons)
                    {
                        lines.Add(head + " down " + button.Name + " -> " + Report(mouse.MouseButton(model.MouseMode, button.Button, pressed: true, 0, cell, model.Columns, model.Rows, model.SgrMouse)));
                        lines.Add(head + " up " + button.Name + " -> " + Report(mouse.MouseButton(model.MouseMode, button.Button, pressed: false, 0, cell, model.Columns, model.Rows, model.SgrMouse)));
                    }
                    foreach (int delta in new[] { 120, -120, 0 })
                    {
                        lines.Add(head + " wheel " + delta + " -> " + Report(mouse.MouseWheel(model.MouseMode, delta <= 0, 0, cell, model.Columns, model.Rows, model.SgrMouse)));
                    }
                    foreach (var button in held)
                    {
                        for (int repeat = 0; repeat < 2; repeat++)
                        {
                            lines.Add(head + " move " + button.Name + " #" + repeat + " -> " + Report(mouse.MouseMove(model.MouseMode, button.Button, 0, cell, model.Columns, model.Rows, model.SgrMouse)));
                        }
                    }
                }
                lines.Add(mode.Name + (sgr ? " sgr" : "") + " outside down -> ");
            }
        }
        return lines;
    }

    private static string Report(byte[] bytes)
    {
        return bytes == null || bytes.Length == 0 ? "" : "MouseReport:" + Escape(Encoding.UTF8.GetString(bytes)) + " ";
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    // Cases whose golden the display model does not give, on purpose; their lines are left out of the comparison on both sides.
    private static readonly Dictionary<string, string> KnownDifferences = new Dictionary<string, string>
    {
        // The WinForms terminal kept a line info with default cells for every line when it showed margins (the timestamps of the Smart
        // Terminal); the WPF view shows none, so a line without style has no line info, which paints the same.
        ["always track lines"] = "the display model keeps no line info for a line without style (AlwaysTrackLines was for the margins of the WinForms view)"
    };

    private static List<string> WithoutCases(IEnumerable<string> lines, Dictionary<string, string> cases)
    {
        List<string> result = new List<string>();
        bool skipping = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("=== ", StringComparison.Ordinal))
            {
                skipping = cases.ContainsKey(line.Substring(4));
            }
            else if (line.StartsWith("random ", StringComparison.Ordinal))
            {
                skipping = false;
            }
            if (!skipping)
            {
                result.Add(line);
            }
        }
        return result;
    }

    private static int Compare(string what, string goldenFile, List<string> lines, Dictionary<string, string> knownDifferences = null)
    {
        string goldenPath = Path.Combine(AppContext.BaseDirectory, goldenFile);
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] golden = File.ReadAllLines(goldenPath);
        if (knownDifferences != null)
        {
            foreach (KeyValuePair<string, string> known in knownDifferences)
            {
                Console.WriteLine("SKIP: " + what + " of \"" + known.Key + "\": " + known.Value);
            }
            golden = WithoutCases(golden, knownDifferences).ToArray();
            lines = WithoutCases(lines, knownDifferences);
        }
        int differences = 0;
        for (int index = 0; index < Math.Max(golden.Length, lines.Count); index++)
        {
            string expected = index < golden.Length ? golden[index] : "<end of golden>";
            string actual = index < lines.Count ? lines[index] : "<end of output>";
            if (expected != actual)
            {
                differences++;
                if (differences <= 20)
                {
                    Console.WriteLine("FAIL: line " + (index + 1) + "\n  golden: " + expected + "\n  actual: " + actual);
                }
            }
        }
        if (differences == 0)
        {
            Console.WriteLine("PASS: " + lines.Count + " lines of " + what + " equal the golden file");
            return 0;
        }
        Console.WriteLine("FAIL: " + differences + " of " + Math.Max(golden.Length, lines.Count) + " lines of " + what + " differ from the golden file");
        return 1;
    }

    private static string Escape(string text)
    {
        StringBuilder result = new StringBuilder();
        foreach (char ch in text)
        {
            if (ch < ' ' || ch == '\\' || ch == '|' || ch >= '\u007f')
            {
                result.Append("\\u").Append(((int)ch).ToString("x4"));
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }

    private static string Hash(string text)
    {
        using SHA1 sha = SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }
}
