using SmarTTY.Controls;
using SmarTTY.CodeEditing;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Drives the real TerminalEmulatorControl with scripted output (and a few keys and pastes) and writes down everything that
// can be seen from outside: the lines of the active buffer with the style of every cell, the cursor, the title, the active
// buffer, the scroll region, the events, and the bytes the terminal sends back (replies to the host, keys, pastes).
// TerminalSnapshotsGolden.txt is what the code wrote before the terminal moved out of the controls (stage 1B); the move must
// not change a single line of it. It is never recorded again.
//
//   dotnet build tests/TerminalSnapshots -c Release
//   xvfb-run -a mono tests/TerminalSnapshots/bin/Release/net48/TerminalSnapshots.exe
//   ... TerminalSnapshots.exe --record <state file> <colours file>      writes the two golden files (old code only)
//
// TerminalSpansGolden.txt (what the view paints with: the brushes of the colour spans of every line, and the cases that use the foreground
// override of the HEX terminal) was written by the code before the colour override of a cell was made a colour (stage 1B, B3a); the state
// golden was written before that and does not change.
//
// The cases are hand-written (full dumps) and generated from a seeded random script (one line each: a summary and a hash of the
// whole dump). The terminal is made 80 x 24 whatever the fonts are.
internal static class Program
{
    private const string Esc = "\u001b";

    private const string Csi = "\u001b[";

    private const string Bel = "\a";

    private const int Columns = 80;

    private const int Rows = 24;

    private static readonly BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo OnKeyDownMethod = typeof(Control).GetMethod("OnKeyDown", AllInstance);

    private static readonly MethodInfo OnKeyPressMethod = typeof(Control).GetMethod("OnKeyPress", AllInstance);

    private static readonly List<string> Prompts = new List<string>();

    // The painted colours of the cases, in the order of the cases (the file TerminalSpansGolden.txt), and of the case that ran last.
    private static readonly List<string> SpanLines = new List<string>();

    private static string LastSpans;

    private sealed class Case
    {
        public string Name;

        // The chunks of output, in the order they arrive.
        public string[] Chunks;

        public Action<TerminalEmulatorControl> Setup;

        // Whether the output is also fed one character at a time: the screen must come out the same.
        public bool AlsoByChar;

        // The keys and pastes are tried after the output (the answer depends on the modes that the output set).
        public bool Probe = true;

        // The case is in the file of the painted colours only (it was added after TerminalSnapshotsGolden.txt was written).
        public bool SpansOnly;
    }

    // Counts the bells (OnBell is the place where the terminal rings, whatever the settings say about the sound).
    private sealed class CountingTerminal : TerminalEmulatorControl
    {
        private readonly Harness _harness;

        public CountingTerminal(Harness harness)
        {
            _harness = harness;
        }

        protected override void OnBell()
        {
            _harness.Bells++;
        }
    }

    private sealed class Harness : IDisposable
    {
        public Form Form;

        public TerminalEmulatorControl Terminal;

        public readonly StringBuilder Sent = new StringBuilder();

        public int Bells;

        public int TitleChanges;

        public int SizeChanges;

        // The exceptions the output made (the type only: the messages differ between runtimes).
        public readonly List<string> Exceptions = new List<string>();

        public void Dispose()
        {
            Form.Dispose();
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // A hang must fail the job instead of blocking it.
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(4));
            Console.WriteLine("FAIL: the terminal snapshots did not finish within 4 minutes");
            Console.Out.Flush();
            // Environment.Exit can itself wait for the main thread, which is what hangs here.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true
        }.Start();

        // The questions of the terminal (a paste of several lines, text the encoding cannot send) are caught and answered "yes".
        ThemedMessageBox.ShowImpl = (text, caption, buttons, icon, owner) =>
        {
            Prompts.Add(Escape(caption + ": " + text) + " [" + buttons + "]");
            switch (buttons)
            {
                case MessageBoxButtons.YesNo:
                case MessageBoxButtons.YesNoCancel:
                    return DialogResult.Yes;
                default:
                    return DialogResult.OK;
            }
        };

        ThemedPasteConfirm.ConfirmImpl = (summary, preview, dangers, offerDontAsk, owner) =>
        {
            Prompts.Add(Escape("paste: " + summary + " / " + preview + " / " + string.Join(",", dangers ?? new string[0])) + " [dontAsk " + offerDontAsk + "]");
            return new ThemedPasteConfirmResult();
        };

        List<string> lines = new List<string>();
        foreach (Case testCase in HandWrittenCases())
        {
            lines.AddRange(RunCase(testCase, full: true));
        }
        lines.AddRange(RunRandomCases(seedCount: 6, casesPerSeed: 60));

        if (args.Length == 3 && args[0] == "--record")
        {
            File.WriteAllLines(args[1], lines);
            File.WriteAllLines(args[2], SpanLines);
            Console.WriteLine("recorded " + lines.Count + " lines to " + args[1] + " and " + SpanLines.Count + " lines to " + args[2]);
            return 0;
        }
        int failed = Compare("terminal state", "TerminalSnapshotsGolden.txt", lines);
        failed += Compare("painted colours", "TerminalSpansGolden.txt", SpanLines);
        return failed == 0 ? 0 : 1;
    }

    // Compares the lines with a golden file next to the program; returns 0 when they are equal.
    private static int Compare(string what, string goldenFile, List<string> lines)
    {
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, goldenFile);
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] golden = File.ReadAllLines(goldenPath);
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

    // ---- running a case -------------------------------------------------------------------------------------------------

    private static void Step(string what)
    {
        if (Environment.GetEnvironmentVariable("TS_VERBOSE") != null)
        {
            Console.WriteLine("  " + what);
        }
    }

    private static Harness MakeHarness(Action<TerminalEmulatorControl> setup)
    {
        Harness harness = new Harness();
        Step("make form");
        harness.Form = new Form
        {
            Width = 1000,
            Height = 700
        };
        harness.Terminal = new CountingTerminal(harness)
        {
            Dock = DockStyle.Fill
        };
        harness.Form.Controls.Add(harness.Terminal);
        Step("show");
        harness.Form.Show();
        Step("fit");
        FitTo(harness, Columns, Rows);
        harness.Terminal.DataEntered += (object sender, byte[] buffer, TerminalEmulatorControl.InputKind kind) =>
        {
            harness.Sent.Append(kind).Append(':').Append(Escape(Encoding.UTF8.GetString(buffer))).Append(' ');
        };
        harness.Terminal.TitleChanged += (object sender, EventArgs e) => harness.TitleChanges++;
        harness.Terminal.TerminalSizeChanged += (TerminalEmulatorControl terminal, int columns, int rows) => harness.SizeChanges++;
        setup?.Invoke(harness.Terminal);
        return harness;
    }

    // The form size that gives an 80 x 24 terminal; found once, because the size of a character depends on the fonts of the machine.
    private static Size _fittedFormSize;

    private static void FitTo(Harness harness, int columns, int rows)
    {
        if (_fittedFormSize != Size.Empty && columns == Columns && rows == Rows)
        {
            harness.Form.Size = _fittedFormSize;
            Application.DoEvents();
        }
        for (int attempt = 0; attempt < 60; attempt++)
        {
            Application.DoEvents();
            Size size = harness.Terminal.TerminalSize;
            Step(DateTime.Now.ToString("HH:mm:ss.fff") + " attempt " + attempt + " size " + size + " form " + harness.Form.Size);
            if (size.Width == columns && size.Height == rows)
            {
                if (columns == Columns && rows == Rows)
                {
                    _fittedFormSize = harness.Form.Size;
                }
                return;
            }
            // About one character of the terminal per step: the width of a column and the height of a line.
            int columnWidth = Math.Max(1, harness.Terminal.ClientSize.Width / Math.Max(1, size.Width));
            int lineHeight = Math.Max(1, harness.Terminal.LineHeight);
            int dx = (columns - size.Width) * columnWidth;
            int dy = (rows - size.Height) * lineHeight;
            if (dx == 0 && columns != size.Width || Math.Abs(columns - size.Width) == 1)
            {
                dx = Math.Sign(columns - size.Width) * Math.Max(1, columnWidth / 2);
            }
            if (Math.Abs(rows - size.Height) == 1)
            {
                dy = Math.Sign(rows - size.Height) * Math.Max(1, lineHeight / 2);
            }
            harness.Form.Size = new Size(Math.Max(100, harness.Form.Width + dx), Math.Max(100, harness.Form.Height + dy));
        }
        Size last = harness.Terminal.TerminalSize;
        throw new InvalidOperationException("The terminal could not be made " + columns + "x" + rows + ", it is " + last.Width + "x" + last.Height);
    }

    private static List<string> RunCase(Case testCase, bool full)
    {
        List<string> output = new List<string>();
        if (Environment.GetEnvironmentVariable("TS_VERBOSE") != null)
        {
            Console.WriteLine("case " + testCase.Name);
        }
        string whole = Run(testCase, byChar: false, probe: testCase.Probe);
        SpanLines.Add("=== " + testCase.Name);
        SpanLines.AddRange(LastSpans.Split('\n'));
        if (testCase.SpansOnly)
        {
            return output;
        }
        output.Add("=== " + testCase.Name);
        if (full)
        {
            output.AddRange(whole.Split('\n'));
        }
        else
        {
            output.Add(Hash(whole));
        }
        if (testCase.AlsoByChar)
        {
            string perChar = Run(testCase, byChar: true, probe: testCase.Probe);
            output.Add(perChar == whole ? "by character: same" : "by character: DIFFERENT " + Hash(perChar));
        }
        return output;
    }

    private static string Run(Case testCase, bool byChar, bool probe)
    {
        using Harness harness = MakeHarness(testCase.Setup);
        Step("output");
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
        Step("dump");
        Application.DoEvents();
        StringBuilder text = new StringBuilder();
        Dump(harness, text);
        LastSpans = DumpSpans(harness);
        Step("probe");
        if (probe)
        {
            harness.Sent.Clear();
            Probe(harness, text);
        }
        return text.ToString().TrimEnd('\n');
    }

    // What the view paints with: the brushes of the colour spans of every line (GetLineColorSpans is what the control asks for
    // when it draws a line), as colours.
    private static string DumpSpans(Harness harness)
    {
        TerminalEmulatorControl terminal = harness.Terminal;
        EditableTextDocument document = (EditableTextDocument)typeof(EditableTextBrowser).GetProperty("EditableDocument", AllInstance).GetValue(terminal);
        MethodInfo getSpans = typeof(TerminalEmulatorBase).GetMethod("GetLineColorSpans", AllInstance);
        StringBuilder text = new StringBuilder();
        for (int line = 0; line < document.LineCount; line++)
        {
            ReadonlyTextBrowser.ColorSpan[] spans = (ReadonlyTextBrowser.ColorSpan[])getSpans.Invoke(terminal, new object[] { line });
            text.Append(line.ToString("D3")).Append('|');
            if (spans == null)
            {
                text.Append("none");
            }
            else
            {
                foreach (ReadonlyTextBrowser.ColorSpan span in spans)
                {
                    text.Append(span.Start).Append('-').Append(span.End).Append(" fg=").Append(DescribeBrush(span.Foreground)).Append(" bg=").Append(DescribeBrush(span.Background))
                        .Append(span.Underline ? " underline" : "").Append("; ");
                }
            }
            text.Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }

    private static string DescribeBrush(Brush brush)
    {
        if (brush == null)
        {
            return "null";
        }
        if (brush is SolidBrush solid)
        {
            return "#" + solid.Color.ToArgb().ToString("X8");
        }
        return brush.GetType().Name;
    }

    private static void Feed(Harness harness, string chunk, int index)
    {
        try
        {
            harness.Terminal.ProcessOutput(chunk);
        }
        catch (Exception exception)
        {
            harness.Exceptions.Add(index + ":" + exception.GetType().Name);
        }
    }

    // ---- what is written down -------------------------------------------------------------------------------------------

    private static void Dump(Harness harness, StringBuilder text)
    {
        TerminalEmulatorControl terminal = harness.Terminal;
        Size size = terminal.TerminalSize;
        text.Append("size ").Append(size.Width).Append('x').Append(size.Height)
            .Append(" buffer ").Append(terminal.ActiveBufferIndex)
            .Append(" cursor ").Append(terminal.CursorPosition.X).Append(',').Append(terminal.CursorPosition.Y)
            .Append(" title ").Append(terminal.Title == null ? "<null>" : "\"" + Escape(terminal.Title) + "\"")
            .Append(" mouse ").Append(terminal.MouseMode)
            .Append(" bells ").Append(harness.Bells)
            .Append(" titleEvents ").Append(harness.TitleChanges)
            .Append(" sizeEvents ").Append(harness.SizeChanges)
            .Append(" directory ").Append(terminal.ReportedDirectory == null ? "<null>" : "\"" + Escape(terminal.ReportedDirectory) + "\"")
            .Append('\n');
        text.Append("style ").Append(DescribeStyle(terminal.CurrentStyle)).Append('\n');
        text.Append("sent ").Append(harness.Sent).Append('\n');
        text.Append("threw ").Append(string.Join(" ", harness.Exceptions)).Append('\n');

        EditableTextDocument document = (EditableTextDocument)typeof(EditableTextBrowser).GetProperty("EditableDocument", AllInstance).GetValue(terminal);
        object buffer = typeof(TerminalEmulatorBase).GetProperty("ActiveBuffer", AllInstance).GetValue(terminal);
        object store = buffer.GetType().GetField("Lines").GetValue(buffer);
        MethodInfo getLine = store.GetType().GetMethod("GetLine");
        for (int line = 0; line < document.LineCount; line++)
        {
            text.Append(line.ToString("D3")).Append('|').Append(Escape(document.GetLineText(line))).Append('|');
            object info = getLine.Invoke(store, new object[] { line });
            if (info == null)
            {
                text.Append("no info");
            }
            else
            {
                text.Append(DescribeLine(info));
            }
            text.Append('\n');
        }
        // Clicking every file link: what the terminal asks to open.
        List<string> clicks = new List<string>();
        EventHandler<OpenFileEventArgs> onClick = (object sender, OpenFileEventArgs e) => clicks.Add(Escape(e.Path) + ":" + e.Line + ":" + e.Column + ":" + e.ColumnUnit);
        terminal.FileLinkClicked += onClick;
        HashSet<object> clicked = new HashSet<object>();
        for (int line = 0; line < document.LineCount; line++)
        {
            object info = getLine.Invoke(store, new object[] { line });
            CellStyle[] cells = info == null ? null : (CellStyle[])info.GetType().GetField("Cells").GetValue(info);
            if (cells == null)
            {
                continue;
            }
            foreach (CellStyle cell in cells)
            {
                object target = cell._link.Target;
                if (target != null && target.GetType().Name == "LinkCandidate" && cell._link.Target.IsActive && clicked.Add(target))
                {
                    cell._link.Target.Activate();
                }
            }
        }
        terminal.FileLinkClicked -= onClick;
        text.Append("clicks ").Append(string.Join(" ", clicks)).Append('\n');
    }

    private static string DescribeLine(object info)
    {
        Type type = info.GetType();
        CellStyle[] cells = (CellStyle[])type.GetField("Cells").GetValue(info);
        StringBuilder text = new StringBuilder();
        text.Append(((bool)type.GetField("IsDimmed").GetValue(info)) ? "dim " : "").Append(((bool)type.GetField("IsWrapped").GetValue(info)) ? "wrap " : "");
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
            PropertyInfo property = target.GetType().GetProperty(name, AllInstance);
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

    // The keys and pastes: the bytes depend on the modes that the output switched (application cursor keys and keypad,
    // bracketed paste, the mouse mode is in the dump).
    private static void Probe(Harness harness, StringBuilder text)
    {
        TerminalEmulatorControl terminal = harness.Terminal;
        Keys[] keys =
        {
            Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Home, Keys.End, Keys.Insert, Keys.Delete, Keys.Tab, Keys.Escape, Keys.Back,
            Keys.F1, Keys.F5, Keys.F12, Keys.Up | Keys.Control, Keys.Left | Keys.Shift, Keys.Right | Keys.Alt, Keys.Return, Keys.NumPad5,
            Keys.PageUp | Keys.Shift
        };
        foreach (Keys key in keys)
        {
            harness.Sent.Clear();
            KeyEventArgs down = new KeyEventArgs(key);
            try
            {
                OnKeyDownMethod.Invoke(terminal, new object[] { down });
            }
            catch (TargetInvocationException exception)
            {
                text.Append("key ").Append(key).Append(" threw ").Append(exception.InnerException.GetType().Name).Append('\n');
                continue;
            }
            text.Append("key ").Append(key).Append(" handled ").Append(down.Handled ? 1 : 0).Append(" suppress ").Append(down.SuppressKeyPress ? 1 : 0)
                .Append(" -> ").Append(harness.Sent).Append('\n');
        }
        foreach (char ch in new[] { 'a', 'Z', '\u00e9', '\u0001', '\r', ' ' })
        {
            harness.Sent.Clear();
            OnKeyPressMethod.Invoke(terminal, new object[] { new KeyPressEventArgs(ch) });
            text.Append("char ").Append(Escape(ch.ToString())).Append(" -> ").Append(harness.Sent).Append('\n');
        }
        foreach (string paste in new[] { "one", "two\nlines", "a\r\nb" })
        {
            harness.Sent.Clear();
            Prompts.Clear();
            terminal.PasteText(paste);
            text.Append("paste ").Append(Escape(paste)).Append(" -> ").Append(harness.Sent).Append(Prompts.Count == 0 ? "" : " asked " + string.Join(" / ", Prompts)).Append('\n');
            Prompts.Clear();
        }
        harness.Sent.Clear();
        terminal.TypeText("ls\r");
        text.Append("TypeText -> ").Append(harness.Sent).Append('\n');
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

    // ---- hand-written cases ---------------------------------------------------------------------------------------------

    private static Case C(string name, params string[] chunks)
    {
        return new Case
        {
            Name = name,
            Chunks = chunks,
            AlsoByChar = true
        };
    }

    private static string Lines(int count, string prefix = "line ")
    {
        StringBuilder text = new StringBuilder();
        for (int index = 1; index <= count; index++)
        {
            text.Append(prefix).Append(index).Append("\r\n");
        }
        return text.ToString();
    }

    private static IEnumerable<Case> HandWrittenCases()
    {
        // Text, wrapping, control characters.
        yield return C("plain text", "Hello, world\r\nsecond line\r\n");
        yield return C("long line wraps", new string('a', 100) + "\r\n" + new string('b', 160) + "X");
        yield return C("exactly the width", new string('c', 80) + "\r\nnext");
        yield return C("auto wrap off", Csi + "?7l" + new string('d', 100) + "\r\nafter" + Csi + "?7h" + new string('e', 90));
        yield return C("lf without cr", "abc\ndef\nghi");
        yield return C("cr overwrite", "hello\rJ");
        yield return C("backspace", "abc\b\bX\b\b\b\bY");
        yield return C("tabs", "a\tb\t\tc\r\n\td" + new string('x', 70) + "\t!");
        yield return C("bell", "a" + Bel + "b" + Bel + Bel);
        yield return C("control characters", "a\u0001b\u0002c\u007fd\u0000e\u000bf\u000cg");
        yield return C("unicode text", "caf\u00e9 \u00fc\u00f1\u00ee \u4e2d\u6587\u5b57 \u0416\u0443\u043a \u0301e\u0301 \U0001F600 end");
        yield return C("wide characters wrap", new string('x', 79) + "\u4e2d\u6587\r\nnext");
        yield return C("combining marks", "e\u0301a\u0300 o\u0302\u0323x");
        yield return C("scrollback", Lines(60));
        yield return C("scroll with full line", Lines(30) + new string('z', 80) + new string('y', 80));

        // Cursor movement.
        yield return C("cursor up down", Lines(5) + Csi + "2A" + "U" + Csi + "3B" + "D" + Csi + "99A" + "T" + Csi + "99B" + "B");
        yield return C("cursor forward back", "0123456789" + Csi + "5D" + "<" + Csi + "2C" + ">" + Csi + "999C" + "E" + Csi + "999D" + "S");
        yield return C("cursor position", Csi + "5;10H" + "A" + Csi + "H" + "B" + Csi + ";20H" + "C" + Csi + "30;100H" + "D" + Csi + "3;4f" + "E" + Csi + "0;0H" + "F");
        yield return C("cursor column and row", Lines(3) + Csi + "40G" + "G" + Csi + "10`" + "H" + Csi + "12d" + "V" + Csi + "1d" + "W");
        yield return C("next and previous line", Lines(3) + Csi + "2E" + "N" + Csi + "3F" + "P" + Csi + "E" + "Q" + Csi + "F" + "R");
        yield return C("save and restore cursor", Csi + "5;5H" + Esc + "7" + Csi + "10;20H" + "X" + Esc + "8" + "Y" + Csi + "8;8H" + Csi + "s" + Csi + "1;1H" + Csi + "u" + "Z");
        yield return C("save restore keeps the style", Csi + "31;1m" + Esc + "7" + Csi + "0m" + "plain" + Esc + "8" + "red");
        yield return C("tab stops", Csi + "3g" + Csi + "1;10H" + Esc + "H" + Csi + "1;20H" + Esc + "H" + Csi + "1;1H" + "a\tb\tc\td" + Csi + "2;1H" + Csi + "I" + "e" + Csi + "2I" + "f" + Csi + "Z" + "g" + Csi + "2Z" + "h");
        yield return C("clear one tab stop", Csi + "1;9H" + Csi + "0g" + Csi + "1;1H" + "a\tb\tc");
        yield return C("index and reverse index", Lines(5) + Esc + "D" + "I" + Esc + "M" + "R" + Csi + "1;1H" + Esc + "M" + "T" + Esc + "E" + "N");
        yield return C("index at bottom scrolls", Csi + "24;1H" + "last" + Esc + "D" + "next" + Esc + "E" + "more");

        // Erasing, inserting, deleting.
        yield return C("erase in line", Lines(1, new string('x', 30) + "      ").Replace("\r\n", "") + Csi + "15G" + Csi + "K" + "\r\n" + new string('y', 30) + Csi + "10G" + Csi + "1K" + "\r\n" + new string('z', 30) + Csi + "10G" + Csi + "2K");
        yield return C("erase in display", Lines(10) + Csi + "5;3H" + Csi + "J" + Csi + "2;1H" + "a");
        yield return C("erase in display to start", Lines(10) + Csi + "5;3H" + Csi + "1J");
        yield return C("erase whole display", Lines(10) + Csi + "2J" + "x");
        yield return C("erase scrollback", Lines(40) + Csi + "3J" + "x");
        yield return C("erase display styled", Csi + "44m" + Lines(5) + Csi + "3;3H" + Csi + "J" + Csi + "0m");
        yield return C("erase characters", "abcdefghij" + Csi + "1G" + Csi + "3C" + Csi + "4X" + "Z");
        yield return C("delete characters", "abcdefghij" + Csi + "1G" + Csi + "2C" + Csi + "3P");
        yield return C("insert characters", "abcdefghij" + Csi + "1G" + Csi + "2C" + Csi + "3@" + "XY");
        yield return C("insert lines", Lines(8) + Csi + "3;1H" + Csi + "2L" + "new");
        yield return C("delete lines", Lines(8) + Csi + "3;1H" + Csi + "2M" + "new");
        yield return C("repeat character", "a" + Csi + "5b" + "b" + Csi + "200b");
        yield return C("insert mode", "abcdef" + Csi + "1G" + Csi + "4h" + "XY" + Csi + "4l" + "Z");
        yield return C("delete more than the line", "abc" + Csi + "1G" + Csi + "999P" + Csi + "999@" + Csi + "999X" + Csi + "999L" + Csi + "999M");

        // Scrolling and regions.
        yield return C("scroll region", Csi + "5;10r" + Csi + "5;1H" + Lines(12) + Csi + "r" + "end");
        yield return C("scroll region cursor home", Csi + "5;10r" + "x");
        yield return C("scroll up and down", Lines(10) + Csi + "2S" + Csi + "1;1H" + "A" + Csi + "3T" + "B");
        yield return C("scroll up in region", Lines(10) + Csi + "3;6r" + Csi + "2S" + Csi + "r");
        yield return C("index in region", Csi + "3;5r" + Csi + "5;1H" + "a" + Esc + "D" + "b" + Esc + "D" + "c" + Csi + "3;1H" + Esc + "M" + "d" + Esc + "M" + "e");
        yield return C("invalid region", Csi + "10;5r" + "a" + Csi + "0;0r" + "b" + Csi + "1;99r" + "c" + Csi + "5;5r" + "d");
        yield return C("insert lines in region", Lines(12) + Csi + "4;8r" + Csi + "5;1H" + Csi + "2L" + Csi + "r");
        yield return C("delete lines outside the region", Lines(12) + Csi + "4;8r" + Csi + "2;1H" + Csi + "2M" + Csi + "r");

        // Rendition.
        yield return C("sgr basic", Csi + "1mbold" + Csi + "22m " + Csi + "4munder" + Csi + "24m " + Csi + "7minv" + Csi + "27m " + Csi + "0mplain");
        yield return C("sgr colours", Csi + "31mred" + Csi + "42mongreen" + Csi + "39m" + "x" + Csi + "49m" + "y" + Csi + "0m" + Csi + "36;45mcyan");
        yield return C("sgr bright colours", Csi + "91mbr" + Csi + "92m" + "bg" + Csi + "103;94mmix" + Csi + "107mwhite" + Csi + "0m");
        yield return C("sgr 256 colours", Csi + "38;5;196mred" + Csi + "48;5;21mblue" + Csi + "38;5;0mblack" + Csi + "38;5;255;48;5;232mgrey" + Csi + "0m" + Csi + "38;5;300mover");
        yield return C("sgr true colours", Csi + "38;2;255;128;0morange" + Csi + "48;2;0;64;128mbg" + Csi + "38;2;1;2mshort" + Csi + "0m" + Csi + "38;2;300;0;0mbig");
        yield return C("sgr colon forms", Csi + "38:5:99mx" + Csi + "38:2::10:20:30my" + Csi + "48:2:1:2:3mz" + Csi + "0m");
        yield return C("sgr reset forms", Csi + "1;31mx" + Csi + "mplain" + Csi + "1;4;7;31;42m" + "all" + Csi + ";m" + "reset");
        yield return C("sgr unknown", Csi + "9mstrike" + Csi + "5mblink" + Csi + "8mhidden" + Csi + "53mover" + Csi + "999mx" + Csi + "0m");
        yield return C("sgr bold bright", Csi + "1;31mbold red" + Csi + "22;31mnormal red" + Csi + "1;21m" + "after 21" + Csi + "1m" + Csi + "22m" + "after 22");
        yield return C("styles survive wrapping and scrolling", Csi + "41m" + Lines(30, new string('r', 70) + " ") + Csi + "0m");
        yield return C("styled blank cells", Csi + "44m" + Csi + "10G" + Csi + "K" + Csi + "2;1H" + Csi + "5@" + Csi + "0m" + "x");

        // Alternate screen.
        yield return C("alternate screen 1049", Lines(10) + Csi + "?1049h" + Csi + "2J" + "in alt" + Csi + "?1049l" + "back");
        yield return C("alternate screen 47", Lines(6) + Csi + "?47h" + "alt" + Csi + "?47l" + "back");
        yield return C("alternate screen 1047", Lines(6) + Csi + "?1047h" + "alt" + Csi + "?1047l" + "back");
        yield return C("alternate screen stays on", Lines(6) + Csi + "?1049h" + "alt text\r\nmore" + Csi + "3;3H" + "x");
        yield return C("alternate screen twice", Lines(6) + Csi + "?1049h" + "a" + Csi + "?1049h" + "b" + Csi + "?1049l" + Csi + "?1049l" + "c");
        yield return C("alternate screen scroll", Csi + "?1049h" + Lines(40) + Csi + "?1049l");
        yield return C("cursor save 1048", "abc" + Csi + "?1048h" + Csi + "5;5H" + "x" + Csi + "?1048l" + "y");

        // Titles and strings.
        yield return C("osc title", Esc + "]0;my title" + Bel + "text");
        yield return C("osc title with st", Esc + "]2;other" + Esc + "\\" + "text");
        yield return C("osc icon name", Esc + "]1;icon" + Bel + "text");
        yield return C("osc title with control characters", Esc + "]0;a\u0001b\tc\u007fd" + Bel + "x");
        yield return C("osc title with semicolons", Esc + "]2;one;two;three" + Bel + "x" + Esc + "]0;" + new string('t', 300) + Bel);
        yield return C("osc without a command", Esc + "]0" + Bel + "x" + Esc + "]x;y" + Bel + "y" + Esc + "];z" + Bel + "z");
        yield return C("osc empty", Esc + "]0;" + Bel + "x");
        yield return C("osc unknown", Esc + "]999;whatever" + Bel + "x" + Esc + "]" + Bel + "y");
        yield return C("osc directory", Esc + "]7;file://host/home/user/dir%20name" + Bel + "x");
        yield return C("osc directory variants", Esc + "]7;file:///tmp/a%20b" + Bel + "1" + Esc + "]7;file://host/" + Bel + "2" + Esc + "]7;/plain/path" + Bel + "3" + Esc + "]7;http://x/y" + Bel + "4" + Esc + "]7;" + Bel + "5" + Esc + "]7;file://h/\u00e9%C3%A9" + Bel + "6");
        yield return C("osc directory plain", Esc + "]9;9;C:\\Users\\me" + Bel + "x");
        yield return C("osc colours", Esc + "]10;?" + Bel + Esc + "]11;?" + Bel + Esc + "]4;1;?" + Bel + Esc + "]10;rgb:ff/00/00" + Bel + "x");
        yield return C("osc clipboard", Esc + "]52;c;aGVsbG8=" + Bel + "x" + Esc + "]52;c;?" + Bel);
        yield return C("osc hyperlink", Esc + "]8;;http://example.com" + Bel + "link" + Esc + "]8;;" + Bel + " after");
        yield return C("osc 133 marks", Esc + "]133;A" + Bel + "$ " + Esc + "]133;B" + Bel + "ls" + Esc + "]133;C" + Bel + "\r\nout\r\n" + Esc + "]133;D;0" + Bel);
        yield return C("dcs ignored", Esc + "Pqsixel data" + Esc + "\\" + "shown" + Esc + "^privacy" + Esc + "\\" + Esc + "_apc" + Esc + "\\" + Esc + "Xsos" + Esc + "\\" + "end");
        yield return C("osc cancelled", Esc + "]0;never" + "\u0018" + "text" + Esc + "]0;never2" + "\u001a" + "more");
        yield return C("osc aborted by esc", Esc + "]0;title" + Esc + "[31mred");

        // Host replies and modes.
        yield return C("device status report", Csi + "5n" + Csi + "5;5H" + Csi + "6n" + Csi + "?6n" + Csi + "7n");
        yield return C("cursor position report in region", Csi + "5;10r" + Csi + "?6h" + Csi + "1;1H" + Csi + "6n" + Csi + "?6l" + Csi + "r");
        yield return C("device attributes", Csi + "c" + Csi + "0c" + Csi + ">c" + Csi + ">0c" + Csi + "=c" + Csi + "?c");
        yield return C("soft reset", Csi + "31;1m" + Csi + "?7l" + Csi + "4h" + Csi + "3;8r" + "x" + Csi + "!p" + "y");
        yield return C("full reset", Lines(10) + Csi + "31m" + Csi + "?1h" + Csi + "?1049h" + "x" + Esc + "c" + "y");
        yield return C("private modes saved and restored", Csi + "?1h" + Csi + "?25l" + Csi + "?1;25;2004s" + Csi + "?1l" + Csi + "?25h" + Csi + "?2004h" + Csi + "?1;25;2004r");
        yield return C("private modes restored without a save", Csi + "?1h" + Csi + "?1r" + Csi + "?2004r" + Csi + "?7r");
        yield return C("private modes saved twice", Csi + "?1h" + Csi + "?1s" + Csi + "?1l" + Csi + "?1s" + Csi + "?1h" + Csi + "?1r");
        yield return C("application cursor keys", Csi + "?1h");
        yield return C("application keypad", Esc + "=");
        yield return C("numeric keypad", Esc + "=" + Esc + ">");
        yield return C("bracketed paste", Csi + "?2004h");
        yield return C("bracketed paste off", Csi + "?2004h" + Csi + "?2004l");
        yield return C("focus reporting", Csi + "?1004h");
        yield return C("mouse mode 9", Csi + "?9h");
        yield return C("mouse mode 1000", Csi + "?1000h");
        yield return C("mouse mode 1002", Csi + "?1002h");
        yield return C("mouse mode 1003", Csi + "?1003h");
        yield return C("mouse mode off again", Csi + "?1003h" + Csi + "?1003l");
        yield return C("mouse encodings", Csi + "?1000h" + Csi + "?1005h" + Csi + "?1006h" + Csi + "?1015h");
        yield return C("cursor visibility", Csi + "?25l" + "x" + Csi + "?25h");
        yield return C("origin mode", Csi + "5;10r" + Csi + "?6h" + Csi + "1;1H" + "a" + Csi + "20;1H" + "b" + Csi + "?6l" + Csi + "1;1H" + "c" + Csi + "r");
        yield return C("line feed mode", Csi + "20h" + "a\nb" + Csi + "20l" + "c\nd");
        yield return C("reverse video mode", Csi + "?5h" + "x" + Csi + "?5l");
        yield return C("unknown modes", Csi + "?9999h" + Csi + "?9999l" + Csi + "99h" + Csi + "99l" + Csi + "?h" + Csi + ">1h" + Csi + "=1h");
        yield return C("multiple modes in one", Csi + "?1;7;25;2004h" + Csi + "?1;2004l");

        // Character sets.
        yield return C("line drawing g0", Esc + "(0" + "lqqqqk\r\nx    x\r\nmqqqqj" + Esc + "(B" + "\r\nlqk");
        yield return C("line drawing g1 shift", Esc + ")0" + "\u000e" + "lqk" + "\u000f" + "lqk");
        yield return C("charset designations", Esc + "(A" + "a" + Esc + "(B" + "b" + Esc + "*0" + "c" + Esc + "+0" + "d" + Esc + "%G" + "e" + Esc + "#8" + "f" + Esc + " F" + "g");
        yield return C("charset cut short", Esc + "(" + "\r\n" + "x" + Esc + "(" + Esc + "[31m" + "y");

        // Broken and split escapes.
        yield return C("split csi", "a" + Esc, "[3", "1", "mred" + Esc, "[", "2J", "z");
        yield return C("split osc", Esc + "]0;ti", "tle", Bel + "x" + Esc + "]2;a" + Esc, "\\" + "y");
        yield return C("split charset", Esc, "(", "0", "lqk");
        yield return C("esc at the end", "abc" + Esc);
        yield return C("csi cancelled by control", Csi + "31;" + "\r\n" + "text" + Csi + "4" + Esc + "[32mgreen" + Csi + "1\u0008" + "x");
        yield return C("csi with intermediates", Csi + "2 q" + "a" + Csi + "?2$p" + "b" + Csi + "1;2\"p" + "c" + Csi + ">4;2m" + "d" + Csi + "<u" + "e");
        yield return C("csi parameter limits", Csi + "99999999999999A" + "a" + Csi + "1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16;17;18;19;20;21;22;23;24;25;26;27;28;29;30m" + "b" + Csi + ";;;;H" + "c");
        yield return C("csi huge repeat", "a" + Csi + "99999999b" + "b");
        yield return C("unterminated csi", Csi + "1;" + new string('9', 300) + "x" + "after");
        yield return C("unterminated osc", Esc + "]0;" + new string('t', 5000) + "after");
        yield return C("lone escapes", Esc + Esc + "[31mred" + Esc + "\rafter" + Esc + "\u0007" + Esc + "q" + Esc + "~");
        yield return C("esc then letters", Esc + "Z" + Esc + "N" + Esc + "O" + Esc + "V" + Esc + "W" + Esc + "\\" + Esc + "k" + Esc + "l" + Esc + "m" + Esc + "n" + Esc + "o" + Esc + "|" + Esc + "}" + Esc + "~");

        // Chunking and unusual settings.
        yield return C("many small chunks", "he", "llo", "\r", "\n", "wor", "ld");
        yield return new Case
        {
            Name = "chunk longer than the limit",
            Chunks = new[] { Lines(40) },
            Setup = terminal => terminal.MaxChunkLength = 100,
            AlsoByChar = false
        };
        yield return new Case
        {
            Name = "unrecognised as hex",
            Chunks = new[] { "a\u0001b\u0002c\u0003d\u0004e" },
            Setup = terminal => terminal.ShowUnrecognizedAsHex = true
        };
        yield return new Case
        {
            Name = "replies suppressed",
            Chunks = new[] { Csi + "5n" + Csi + "6n" + Csi + "c" },
            Setup = terminal => terminal.SuppressReplies = true
        };
        yield return new Case
        {
            Name = "enter sequence",
            Chunks = new[] { "x" },
            Setup = terminal => terminal.EnterSequence = "\r\n"
        };
        yield return new Case
        {
            Name = "tab width",
            Chunks = new[] { "a\tb\tc" },
            Setup = terminal => terminal.TabWidth = 4
        };
        yield return new Case
        {
            Name = "log levels",
            Chunks = new[] { "INFO ok\r\nWARNING careful\r\nERROR broken\r\n[ERROR] again\r\nplain\r\n" },
            Setup = terminal => terminal.HighlightLogLevels = true
        };
        yield return new Case
        {
            Name = "links",
            Chunks = new[] { "see https://example.com/path?x=1 and /etc/passwd:12 and C:\\Windows\\notepad.exe(3) or file.txt:4:5\r\nhttps://very.long.example.com/" + new string('a', 90) + "\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = true;
            }
        };
        yield return new Case
        {
            Name = "links relative to the reported directory",
            Chunks = new[] { Esc + "]7;file://host/home/user/project" + Bel + "src/main.c:10:5: error: x\r\n./run.sh:3\r\nREADME.md and Makefile and lib/util.h(7,2)\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = true;
                terminal.DirectoryFallback = () => "/fallback/dir";
            }
        };
        yield return new Case
        {
            Name = "links relative to the fallback directory",
            Chunks = new[] { "src/main.c:10:5: error: x\r\nMakefile\r\nrun.sh:3\r\n./a.out\r\n../lib/b.c:4\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = true;
                terminal.DirectoryFallback = () => "/fallback/dir";
            }
        };
        yield return new Case
        {
            Name = "no links on the alternate screen",
            Chunks = new[] { Csi + "?1049h" + "see https://example.com/path and /etc/passwd:12\r\n" + Csi + "?1049l" + "https://back.example.com/ /etc/hosts:1\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = true;
            }
        };
        yield return new Case
        {
            Name = "no links while the mouse is taken",
            Chunks = new[] { Csi + "?1000h" + "https://example.com/path /etc/passwd:12\r\n" + Csi + "?1000l" + "https://back.example.com/\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = true;
            }
        };
        yield return new Case
        {
            Name = "urls only",
            Chunks = new[] { "(https://example.com/a_(b)) <http://x.y/z>, ftp://f.g/h. s3://bucket/key git+ssh://host/x https://" },
            Setup = terminal =>
            {
                terminal.UrlLinks = true;
                terminal.FileLinks = false;
            }
        };
        yield return new Case
        {
            Name = "files only",
            Chunks = new[] { "/usr/bin/env: bad\r\nbash: /tmp: Is a directory\r\n/etc/passwd:3 a.cpp:12:3 b.py:1 c.txt(4) d.rs:5:6\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = false;
                terminal.FileLinks = true;
            }
        };
        yield return new Case
        {
            Name = "links off",
            Chunks = new[] { "see https://example.com/path and /etc/passwd:12\r\n" },
            Setup = terminal =>
            {
                terminal.UrlLinks = false;
                terminal.FileLinks = false;
            }
        };
        yield return new Case
        {
            Name = "always track lines",
            Chunks = new[] { "plain\r\n" + Csi + "31mred" + Csi + "0m\r\n" },
            Setup = terminal => terminal.AlwaysTrackLines = true
        };
        yield return new Case
        {
            Name = "typed text goes to the host",
            Chunks = new[] { "$ " },
            Setup = terminal => terminal.InputEncoding = Encoding.GetEncoding("iso-8859-1")
        };
        yield return new Case
        {
            Name = "full screen program",
            Chunks = new[]
            {
                Csi + "?1049h" + Csi + "?25l" + Csi + "2J" + Csi + "H" + Csi + "?1h" + Esc + "=" + Csi + "?1000h" + Csi + "?1006h",
                Csi + "1;1H" + Csi + "7m" + "  Title bar" + new string(' ', 68) + Csi + "0m",
                Csi + "2;1H" + "~" + Csi + "3;1H" + "~" + Csi + "24;1H" + Csi + "1;37;41m" + "-- INSERT --" + Csi + "0m" + Csi + "24;14H",
                Csi + "?25h" + Csi + "2 q" + Csi + "?2004h" + Csi + "?1004h"
            }
        };
        yield return new Case
        {
            Name = "colourful ls and prompt",
            Chunks = new[]
            {
                Csi + "01;32muser@host" + Csi + "00m:" + Csi + "01;34m~/dir" + Csi + "00m$ ls --color\r\n",
                Csi + "01;34mdir" + Csi + "0m  " + Csi + "01;32mrun.sh" + Csi + "0m  " + Csi + "0;31mfile.tar.gz" + Csi + "0m  plain.txt\r\n",
                Csi + "01;32muser@host" + Csi + "00m:" + Csi + "01;34m~/dir" + Csi + "00m$ " + Csi + "K"
            }
        };
        yield return new Case
        {
            Name = "override colour",
            Chunks = new[] { "echo text" + Csi + "1m" + "bold" + Csi + "0m" + "plain\r\nsecond" },
            Setup = terminal => terminal.CurrentStyle = OverrideStyle(0xFFCC8800u),
            SpansOnly = true
        };
        yield return new Case
        {
            Name = "override with transparent colour",
            Chunks = new[] { "echo text" },
            Setup = terminal => terminal.CurrentStyle = OverrideStyle(0x00000000u),
            SpansOnly = true
        };
        yield return new Case
        {
            Name = "override without a colour",
            Chunks = new[] { "echo text\r\n" + Csi + "31mred" },
            Setup = terminal => terminal.CurrentStyle = OverrideStyle(null),
            SpansOnly = true
        };
        yield return new Case
        {
            Name = "override over log levels and colours",
            Chunks = new[] { "ERROR failed\r\nWARNING careful\r\n" + Csi + "7m" + "inverse" + Csi + "0m" + "\r\n" + Csi + "38;5;196m" + "red" },
            Setup = terminal =>
            {
                terminal.HighlightLogLevels = true;
                terminal.CurrentStyle = OverrideStyle(0xFF00AA55u);
            },
            SpansOnly = true
        };
        yield return new Case
        {
            Name = "two override colours",
            Chunks = new[] { "first" },
            Setup = terminal => terminal.CurrentStyle = OverrideStyle(0xFF112233u),
            SpansOnly = true
        };
        yield return new Case
        {
            Name = "progress bar redraw",
            Chunks = new[] { "[=     ] 10%\r", "[===   ] 50%\r", "[======] 100%\r\ndone\r\n" + Csi + "2K" + "\rnext" + Csi + "1A" + Csi + "2K" + "up" }
        };
    }

    // A style with the foreground override that the HEX terminal sets for the echo of what was typed. The override holds a brush
    // in the code before stage 1B and a colour after it; this makes either.
    private static CellStyle OverrideStyle(uint? argb)
    {
        Type type = typeof(CellStyle.ForegroundOverride);
        object value = Activator.CreateInstance(type);
        if (argb.HasValue)
        {
            FieldInfo brush = type.GetField("Brush");
            if (brush != null)
            {
                brush.SetValue(value, new SolidBrush(Color.FromArgb(unchecked((int)argb.Value))));
            }
            else
            {
                type.GetField("Color").SetValue(value, new RgbColor(argb.Value));
            }
        }
        CellStyle style = default;
        style.Override = (CellStyle.ForegroundOverride)value;
        return style;
    }

    // ---- generated cases ------------------------------------------------------------------------------------------------

    private static readonly string[] Fragments =
    {
        "text", "Some longer text that may wrap around the edge of the screen when it is repeated a few times over", "caf\u00e9", "\u4e2d\u6587", "\U0001F600",
        "e\u0301", "\r", "\n", "\r\n", "\b", "\t", Bel, "\u000e", "\u000f", "\u007f", "\u0001",
        "[1m", "[0m", "[m", "[4m", "[7m", "[22m", "[24m", "[27m", "[31m", "[32;1m", "[42m", "[39m", "[49m", "[91m", "[101m", "[38;5;123m", "[48;5;9m",
        "[38;2;10;20;30m", "[48;2;200;100;50m", "[0;1;31;42m",
        "[A", "[3A", "[B", "[4B", "[C", "[7C", "[D", "[9D", "[E", "[2E", "[F", "[G", "[20G", "[H", "[5;6H", "[12;40H", "[;9H", "[30;90H", "[d", "[7d", "[f", "[2;2f",
        "[J", "[1J", "[2J", "[3J", "[K", "[1K", "[2K", "[3X", "[X", "[2P", "[P", "[4@", "[@", "[2L", "[L", "[3M", "[M", "[2S", "[S", "[2T", "[T", "[5b", "[Z", "[2Z", "[I", "[3I",
        "[g", "[3g", "[0g", "H", "7", "8", "D", "M", "E", "c", "=", ">", "[s", "[u",
        "[r", "[3;10r", "[5;6r", "[1;24r", "[?6h", "[?6l", "[?7h", "[?7l", "[4h", "[4l", "[20h", "[20l", "[?25l", "[?25h", "[?1h", "[?1l", "[?2004h", "[?2004l",
        "[?1;25s", "[?1;25r", "[?2004r", "[21m", "[1;21m", "]0;a\u0001b\a", "]7;file://h/p%20q\a", "]0\a", "[?1000h", "[?1000l", "[?1002h", "[?1003h", "[?1006h", "[?1006l", "[?47h", "[?47l", "[?1047h", "[?1047l", "[?1048h", "[?1048l", "[?1049h", "[?1049l", "[?1004h",
        "[!p", "[5n", "[6n", "[c", "[>c", "[?6n",
        "]0;title\a", "]2;other title\u001b\\", "]7;file:///tmp/x\a", "]133;A\a", "]8;;http://a.b/c\a", "]8;;\a",
        "(0", "(B", ")0", "(", "[", "]0;unterminated", "[31", "[?", "[1;", "P1$r", "\\", "^x\u001b\\",
        "https://example.com/a?b=1 ", "/var/log/syslog:42 ", "ERROR something broke ", "WARNING careful "
    };

    private static IEnumerable<Case> RandomCases(int seed, int count)
    {
        Random random = new Random(seed);
        for (int caseIndex = 0; caseIndex < count; caseIndex++)
        {
            int pieces = random.Next(5, 60);
            StringBuilder stream = new StringBuilder();
            for (int piece = 0; piece < pieces; piece++)
            {
                string fragment = Fragments[random.Next(Fragments.Length)];
                bool plain = fragment.Length > 0 && (fragment[0] == '\r' || fragment[0] == '\n' || fragment[0] == '\b' || fragment[0] == '\t' || fragment[0] == '\a' || fragment[0] > '\u007e' || char.IsLetter(fragment[0]) && fragment.Length > 2 && fragment != "text" && fragment[0] != 'P');
                bool alreadyEscape = fragment.Length > 0 && (fragment[0] == '\u000e' || fragment[0] == '\u000f' || fragment[0] == '\u007f' || fragment[0] == '\u0001' || plain);
                if (!alreadyEscape)
                {
                    stream.Append(Esc);
                }
                stream.Append(fragment);
            }
            string all = stream.ToString();
            // The stream arrives in pieces cut at random places, escape sequences included.
            List<string> chunks = new List<string>();
            int position = 0;
            while (position < all.Length)
            {
                int length = random.Next(4) == 0 ? random.Next(1, 8) : random.Next(1, all.Length - position + 1);
                length = Math.Min(length, all.Length - position);
                chunks.Add(all.Substring(position, length));
                position += length;
            }
            bool links = random.Next(2) == 0;
            bool highlight = random.Next(3) == 0;
            yield return new Case
            {
                Name = "random " + seed + "/" + caseIndex,
                Chunks = chunks.ToArray(),
                Setup = terminal =>
                {
                    terminal.UrlLinks = links;
                    terminal.FileLinks = links;
                    terminal.HighlightLogLevels = highlight;
                },
                Probe = true
            };
        }
    }

    private static List<string> RunRandomCases(int seedCount, int casesPerSeed)
    {
        List<string> output = new List<string>();
        for (int seed = 1; seed <= seedCount; seed++)
        {
            foreach (Case testCase in RandomCases(seed, casesPerSeed))
            {
                string dump = Run(testCase, byChar: false, probe: true);
                SpanLines.Add(testCase.Name + " " + Hash(LastSpans));
                string[] dumpLines = dump.Split('\n');
                output.Add(testCase.Name + " " + dumpLines[0] + " lines " + dumpLines.Count(line => line.Length > 3 && char.IsDigit(line[0]) && line[3] == '|') + " " + Hash(dump));
            }
        }
        return output;
    }
}
