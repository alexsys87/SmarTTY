using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Checks of the model of the WPF terminal view (Terminal/Rendering/): the colours a cell is painted with (the rule of the WinForms view,
// whose painted colours TerminalSnapshots/TerminalSpansGolden.txt holds), the runs a row is drawn with, the queue of the output of a frame,
// and the display model driven by the real emulator and screen.
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static string Hex(RgbColor? color)
    {
        return color.HasValue ? "#" + color.Value.Argb.ToString("X8") : "null";
    }

    private sealed class LinkHost : IOutputLinkHost
    {
        public string OpenedUrl;

        public string OpenedFile;

        public bool IsLinkExtension(string extension)
        {
            return extension == ".c";
        }

        public void RequestOpenFile(string path, int? line, int? column, SmarTTY.CodeEditing.ColumnUnit unit)
        {
            OpenedFile = path + ":" + line;
        }

        public void RequestOpenUrl(string url)
        {
            OpenedUrl = url;
        }
    }

    private sealed class Target : ILinkTarget
    {
        public bool IsActive { get; set; }

        public void Activate()
        {
        }
    }

    private static int Main()
    {
        try
        {
            ColorChecks();
            RunChecks();
            QueueChecks();
            DisplayModelChecks();
            BenchmarkScriptChecks();
            ClassicTerminalChecks();
            SearchChecks();
            ThreadingSourceChecks();
            PromptLineChecks();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message.StartsWith("FAIL") ? exception.Message : "FAIL: " + exception);
            return 1;
        }
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }

    private static void ColorChecks()
    {
        TerminalColorScheme scheme = new TerminalColorScheme();
        ResolvedCellColors colors = scheme.Resolve(CellStyle.Default);
        Equal(Hex(colors.Foreground) + " " + Hex(colors.Background) + " " + colors.Underline, "null null False", "default style: no colours");
        Equal(Hex(scheme.Resolve(new CellStyle { Foreground = 2 }).Foreground), "#FFBB0000", "red");
        Equal(Hex(scheme.Resolve(new CellStyle { Foreground = 2, Bold = true }).Foreground), "#FFFF5555", "bold red is bright red (the golden's colour of ESC[1;31m)");
        Equal(Hex(scheme.Resolve(new CellStyle { Bold = true }).Foreground), "#FFFFFFFF", "bold without a colour: the bold default colour");
        Equal(Hex(scheme.Resolve(new CellStyle { Background = 5 }).Background), "#FF0000BB", "blue background (the golden's colour of ESC[44m)");
        colors = scheme.Resolve(new CellStyle { Inverse = true });
        Equal(Hex(colors.Foreground) + " " + Hex(colors.Background), "#FF000000 #FFD3D3D3", "inverse without colours: the background on the text colour (as in the golden)");
        colors = scheme.Resolve(new CellStyle { Inverse = true, Foreground = 2, Background = 3 });
        Equal(Hex(colors.Foreground) + " " + Hex(colors.Background), "#FF00BB00 #FFBB0000", "inverse swaps the colours");
        Equal(Hex(scheme.Resolve(new CellStyle { ExtendedForeground = 2 }).Foreground), "#FFBB0000", "256 colours: 1 is the palette's red");
        Equal(Hex(scheme.Resolve(new CellStyle { ExtendedForeground = 10 }).Foreground), "#FFFF5555", "256 colours: 9 is the palette's bright red");
        Equal(Hex(scheme.Resolve(new CellStyle { ExtendedForeground = 197 }).Foreground), "#FFFF0000", "256 colours: 196 is pure red");
        Equal(Hex(scheme.Resolve(new CellStyle { ExtendedForeground = 233 }).Foreground), "#FF080808", "256 colours: 232 is the first grey");
        Equal(Hex(scheme.Resolve(new CellStyle { ExtendedBackground = XtermProtocol.RgbColorFlag | 0x123456 }).Background), "#FF123456", "24-bit background");
        Equal(Hex(scheme.Resolve(new CellStyle { Foreground = 2, Override = new CellStyle.ForegroundOverride { Color = new RgbColor(0xFF00FF00) } }).Foreground), "#FF00FF00", "the override wins");
        Equal(Hex(scheme.Resolve(new CellStyle { Foreground = 2, Override = new CellStyle.ForegroundOverride() }).Foreground), "#FFBB0000", "an override without a colour changes nothing");
        CellStyle error = new CellStyle();
        error._link.Severity = 3;
        Equal(Hex(scheme.Resolve(error).Foreground), "#FFFF5555", "an error without a colour of its own is bright red");
        error._link.Severity = 2;
        Equal(Hex(scheme.Resolve(error).Foreground), "#FFFFFF55", "a warning is bright yellow");
        error._link.Severity = 1;
        Equal(Hex(scheme.Resolve(error).Foreground), "#FF55FFFF", "a note is bright cyan");
        error.Foreground = 5;
        Equal(Hex(scheme.Resolve(error).Foreground), "#FF0000BB", "the program's own colour wins over the severity");
        CellStyle link = new CellStyle();
        Target target = new Target();
        link._link.Target = target;
        Check(!scheme.Resolve(link).Underline, "an inactive link is not underlined");
        target.IsActive = true;
        Check(scheme.Resolve(link).Underline, "an active link is underlined");
        Check(scheme.Resolve(new CellStyle { Underline = true }).Underline, "SGR 4 underlines");
        scheme.SetPaletteColor(1, bright: false, new RgbColor(0xFF112233));
        Equal(Hex(scheme.Resolve(new CellStyle { Foreground = 2 }).Foreground), "#FF112233", "a palette colour can be set");
        Equal(Hex(scheme.GetPaletteColor(9, bright: false)), "null", "a number past the palette has no colour");
    }

    private static LineInfo Line(params CellStyle[] cells)
    {
        return new LineInfo { Cells = cells };
    }

    private static CellStyle[] Cells(string pattern, Dictionary<char, CellStyle> styles)
    {
        return pattern.Select(ch => styles.TryGetValue(ch, out CellStyle style) ? style : CellStyle.Default).ToArray();
    }

    private static string Describe(RowLayout layout)
    {
        StringBuilder builder = new StringBuilder();
        foreach (BackgroundRun run in layout.Backgrounds)
        {
            builder.Append("bg ").Append(run.Start).Append('+').Append(run.Length).Append(' ').Append(Hex(run.Color)).Append("; ");
        }
        foreach (TextRun run in layout.Texts)
        {
            builder.Append('"').Append(layout.Text.Substring(run.Start, run.Length)).Append("\" ").Append(run.Start).Append(' ').Append(Hex(run.Foreground));
            if (run.Underline)
            {
                builder.Append(" u");
            }
            builder.Append("; ");
        }
        return builder.ToString().TrimEnd();
    }

    private static void RunChecks()
    {
        TerminalColorScheme scheme = new TerminalColorScheme();
        RowLayout layout = new RowLayout();
        CellStyle red = new CellStyle { Foreground = 2 };
        CellStyle blue = new CellStyle { Background = 5 };
        CellStyle redOnBlue = new CellStyle { Foreground = 2, Background = 5 };
        CellStyle under = new CellStyle { Underline = true };
        Dictionary<char, CellStyle> styles = new Dictionary<char, CellStyle> { ['r'] = red, ['b'] = blue, ['x'] = redOnBlue, ['u'] = under };

        RowRunBuilder.Build("hello world", null, scheme, 80, layout);
        Equal(Describe(layout), "\"hello world\" 0 #FFD3D3D3;", "plain text is one run: the blank does not split it");
        RowRunBuilder.Build("  ab   ", null, scheme, 80, layout);
        Equal(Describe(layout), "\"ab\" 2 #FFD3D3D3;", "a run neither starts nor ends with blanks");
        RowRunBuilder.Build("", null, scheme, 80, layout);
        Equal(Describe(layout), "", "an empty line has no runs");
        RowRunBuilder.Build("     ", null, scheme, 80, layout);
        Equal(Describe(layout), "", "a line of blanks has no runs");
        RowRunBuilder.Build(null, null, scheme, 80, layout);
        Equal(layout.Text, "", "a missing line is empty");

        RowRunBuilder.Build("a RED b", Line(Cells("  rrr  ", styles)), scheme, 80, layout);
        Equal(Describe(layout), "\"a\" 0 #FFD3D3D3; \"RED\" 2 #FFBB0000; \"b\" 6 #FFD3D3D3;", "a coloured word is a run of its own");
        RowRunBuilder.Build("ab cd", Line(Cells("rr r", styles)), scheme, 80, layout);
        Equal(Describe(layout), "\"ab c\" 0 #FFBB0000; \"d\" 4 #FFD3D3D3;", "a blank between cells of one colour stays in the run; cells past the styles are default");
        RowRunBuilder.Build("ab cd", Line(Cells("rr rr", styles)), scheme, 80, layout);
        Equal(layout.Texts.Count, 1, "one colour over a blank of another style: one glyph run");

        RowRunBuilder.Build("abcdef", Line(Cells("bbxxbb", styles)), scheme, 80, layout);
        Equal(Describe(layout), "bg 0+6 #FF0000BB; \"ab\" 0 #FFD3D3D3; \"cd\" 2 #FFBB0000; \"ef\" 4 #FFD3D3D3;", "one background rectangle under three text runs");
        RowRunBuilder.Build("ab  ", Line(Cells("bb b", styles)), scheme, 80, layout);
        Equal(Describe(layout), "bg 0+2 #FF0000BB; bg 3+1 #FF0000BB; \"ab\" 0 #FFD3D3D3;", "a background is painted under blanks, and a gap splits it");

        RowRunBuilder.Build("a b", Line(Cells("uuu", styles)), scheme, 80, layout);
        Equal(Describe(layout), "\"a b\" 0 #FFD3D3D3 u;", "an underlined blank belongs to the run");
        RowRunBuilder.Build("ab  cd", Line(Cells("uu  uu", styles)), scheme, 80, layout);
        Equal(Describe(layout), "\"ab\" 0 #FFD3D3D3 u; \"cd\" 4 #FFD3D3D3 u;", "blanks without an underline split underlined runs");
        RowRunBuilder.Build("abcd", Line(Cells("uu", styles)), scheme, 80, layout);
        Equal(Describe(layout), "\"ab\" 0 #FFD3D3D3 u; \"cd\" 2 #FFD3D3D3;", "the underline splits a run");

        LineInfo dimmed = Line(Cells("xx", styles));
        dimmed.IsDimmed = true;
        RowRunBuilder.Build("ab", dimmed, scheme, 80, layout);
        Equal(Describe(layout), "\"ab\" 0 #FFA9A9A9;", "a dimmed line: the inactive colour, no background");

        RowRunBuilder.Build("abcdef", Line(Cells("rrrrrr", styles)), scheme, 3, layout);
        Equal(Describe(layout) + " " + layout.Text, "\"abc\" 0 #FFBB0000; abc", "only the columns of the view are painted");

        RowRunBuilder.Build("abc", Line(Cells("rrr", styles)), scheme, 80, layout);
        ulong first = layout.Signature;
        RowRunBuilder.Build("abc", Line(Cells("rrr", styles)), scheme, 80, layout);
        Equal(layout.Signature, first, "the same row has the same signature");
        RowRunBuilder.Build("abd", Line(Cells("rrr", styles)), scheme, 80, layout);
        Check(layout.Signature != first, "another text: another signature");
        RowRunBuilder.Build("abc", Line(Cells("rrx", styles)), scheme, 80, layout);
        Check(layout.Signature != first, "another background: another signature");
        RowRunBuilder.Build("abc", Line(Cells("rrb", styles)), scheme, 80, layout);
        Check(layout.Signature != first, "another colour: another signature");
        RowRunBuilder.Build("abc", dimmed, scheme, 80, layout);
        ulong dim = layout.Signature;
        RowRunBuilder.Build("abc", Line(new CellStyle { Foreground = 0 }), scheme, 80, layout);
        Check(layout.Signature != dim, "a dimmed row differs from a plain one");
    }

    private static void QueueChecks()
    {
        FrameOutputQueue queue = new FrameOutputQueue();
        Equal(queue.Take(), (string)null, "an empty queue gives nothing");
        Check(queue.Append("abc"), "the first append asks for a frame");
        Check(!queue.Append("def"), "a second append finds the frame asked for");
        Check(!queue.Append(""), "an empty append asks for nothing");
        Equal(queue.Count, 6, "the count");
        Equal(queue.Take(4), "abcd", "a piece of the given length");
        Equal(queue.Count, 2, "the count after a piece");
        Check(!queue.Append("gh"), "an append while a backlog is being taken does not ask again");
        Equal(queue.Take(4), "ef", "the rest of the backlog first");
        Equal(queue.Take(), "gh", "then what came after it");
        Equal(queue.Take(), (string)null, "then nothing");
        Check(queue.Append("x"), "an append after the queue was emptied asks for a frame again");
        queue.Clear();
        Equal(queue.Count, 0, "clear drops everything");
        queue.Append("ab😀cd");
        Equal(queue.Take(3), "ab", "a piece does not end with the first half of a surrogate pair");
        Equal(queue.Take(2), "😀", "the pair comes whole");
        Equal(queue.Take(0), "cd", "0: everything");
        queue.Append("\uD83D");
        Equal(queue.Take(1), "\uD83D", "a lone first half is given when it is all there is");

        FrameOutputQueue shared = new FrameOutputQueue();
        Parallel.For(0, 8, worker =>
        {
            for (int index = 0; index < 1000; index++)
            {
                shared.Append("0123456789");
            }
        });
        int total = 0;
        string piece;
        while ((piece = shared.Take(777)) != null)
        {
            total += piece.Length;
        }
        Equal(total, 80000, "appends from several threads: nothing is lost");
    }

    private static void DisplayModelChecks()
    {
        TerminalDisplayModel model = new TerminalDisplayModel(10, 3);
        int changes = 0;
        model.Changed += () => changes++;
        model.ProcessOutput("hello\r\n\u001b[31mred\u001b[0m");
        Equal(model.Document.GetLineText(0), "hello", "the first row");
        Equal(model.Document.GetLineText(1), "red", "the second row");
        Check(changes > 0 && model.Version > 0, "the output changed the model");
        TerminalColorScheme scheme = new TerminalColorScheme();
        RowLayout layout = new RowLayout();
        RowRunBuilder.Build(model.Document.GetLineText(1), model.Lines.GetLine(1), scheme, model.Columns, layout);
        Equal(Describe(layout), "\"red\" 0 #FFBB0000;", "the emulator's colours reach the runs");
        Equal(model.Caret.Line + ":" + model.Caret.Column, "1:3", "the caret follows the cursor");

        string reply = null;
        model.Reply += text => reply = text;
        model.ProcessOutput("\u001b[6n");
        Equal(reply, "\u001b[2;4R", "a cursor position query is answered");
        reply = null;
        model.SuppressReplies = true;
        model.ProcessOutput("\u001b[6n");
        Equal(reply, (string)null, "no answer when the replies are suppressed");

        int titles = 0;
        model.TitleChanged += () => titles++;
        model.ProcessOutput("\u001b]0;my title\u0007");
        Equal(model.Title + " " + titles, "my title 1", "the title");

        model.ProcessOutput("\r\n4\r\n5\r\n6\r\n7");
        Equal(model.ScreenTopLine, 3, "the screen moved down with the output");
        Equal(model.TopLine, 3, "the view follows the screen");
        Equal(model.Document.GetLineText(model.TopLine + 2), "7", "the last row is at the bottom of the view");
        model.TopLine = 0;
        Equal(model.TopLine, 0, "the view can be scrolled back");
        model.TopLine = 100;
        Equal(model.TopLine, model.MaxTopLine, "and not past the end");
        model.TopLine = -5;
        Equal(model.TopLine, 0, "or before the start");
        model.ProcessOutput("8");
        Equal(model.TopLine, 3, "output brings the caret back into the view");
        model.TopLine = 0;
        model.ScrollToScreen();
        Equal(model.TopLine, model.ScreenTopLine, "a key brings the screen back");

        model.ProcessOutput("\u001b[?1h\u001b=");
        Check(model.ApplicationCursorKeys && model.ApplicationKeypad, "application cursor keys and keypad");
        model.ProcessOutput("\u001b[?1l\u001b>");
        Check(!model.ApplicationCursorKeys && !model.ApplicationKeypad, "and back");
        model.ProcessOutput("\u001b[?2004h");
        Check(model.BracketedPaste, "bracketed paste");
        int mouseChanges = 0;
        model.MouseModeChanged += () => mouseChanges++;
        model.ProcessOutput("\u001b[?1000h\u001b[?1006h");
        Check(model.MouseMode != MouseReportingMode.Off && model.SgrMouse && mouseChanges == 1, "mouse reporting with SGR coordinates");
        model.ProcessOutput("\u001b[?1000l");
        Check(model.MouseMode == MouseReportingMode.Off, "mouse reporting off");

        model.ProcessOutput("\u001b[?1049h");
        Equal(model.ActiveBufferIndex, 1, "the alternate screen");
        model.ProcessOutput("\u001b[2;2Hx");
        Equal(model.Document.GetLineText(1).TrimEnd(), " x", "it has a document of its own");
        model.ProcessOutput("\u001b[?1049l");
        Equal(model.ActiveBufferIndex + " " + model.Document.GetLineText(0), "0 hello", "back on the normal screen with its text");

        TerminalDisplayModel wrap = new TerminalDisplayModel(5, 3);
        wrap.ProcessOutput("abcdefgh\r\nxy");
        Equal(wrap.Document.GetLineText(0) + "|" + wrap.Document.GetLineText(1), "abcde|fgh", "a long line wraps");
        Check(!wrap.HasSelection, "no selection at first");
        wrap.Select(new TextDocument.TextPosition(0, 0), new TextDocument.TextPosition(2, 2));
        Equal(wrap.GetSelectedText(), "abcdefgh\r\nxy", "a wrapped line is copied without a line break");
        wrap.Select(new TextDocument.TextPosition(2, 2), new TextDocument.TextPosition(1, 0));
        wrap.GetSelection(out TextDocument.TextPosition start, out TextDocument.TextPosition end);
        Equal(start.Line + ":" + start.Column + "-" + end.Line + ":" + end.Column, "0:1-2:2", "a selection made backwards is given in order");
        wrap.Select(new TextDocument.TextPosition(99, 99), new TextDocument.TextPosition(0, 0));
        wrap.GetSelection(out start, out end);
        Equal(end.Line + ":" + end.Column, "2:2", "a selection is kept inside the document");
        wrap.ClearSelection();
        Check(!wrap.HasSelection && wrap.GetSelectedText() == "", "the selection is cleared");

        wrap.Resize(8, 4);
        Equal(wrap.Columns + "x" + wrap.Rows, "8x4", "the grid has the new size");
        wrap.ProcessOutput("\r\n12345678");
        Check(Enumerable.Range(0, wrap.Document.LineCount).Any(line => wrap.Document.GetLineText(line) == "12345678"), "the new width is used (8 characters on one row)");
        Equal(wrap.Caret.Column, 0, "the cursor waits at the edge: the caret is at the start of the next row");

        TerminalDisplayModel reset = new TerminalDisplayModel(10, 3);
        reset.ProcessOutput("abc\u001b[?1049h\u001b[?1h");
        reset.Reset();
        Check(reset.ActiveBufferIndex == 0 && !reset.ApplicationCursorKeys && reset.Document.GetLineText(0) == "", "a reset: the normal screen, the modes, an empty screen");
        reset.ProcessOutput("\u001b[31mab\u001bc");
        Check(reset.Document.LineCount <= 1 && reset.Document.GetLineText(0).Trim() == "", "ESC c resets too");

        TerminalDisplayModel hidden = new TerminalDisplayModel(10, 3);
        hidden.ProcessOutput("\u001b[?25l");
        Check(hidden.CaretHidden, "the program hides the caret");
        hidden.ProcessOutput("\u001b[?25h");
        Check(!hidden.CaretHidden, "and shows it");
        int bells = 0;
        hidden.Bell += () => bells++;
        hidden.ProcessOutput("\u0007");
        Equal(bells, 1, "the bell");
        string chunks = "";
        hidden.OutputReceived += text => chunks += text;
        hidden.ProcessOutput("a\u001b[");
        hidden.ProcessOutput("1mb");
        Equal(chunks, "a\u001b[1mb", "the chunks are given as they came");
        Equal(hidden.Document.GetLineText(0), "ab", "a sequence cut between two chunks is put together");
    }

    // The scripts of the lab window are the same on every run and go through the emulator without an error.
    private static void BenchmarkScriptChecks()
    {
        TerminalColorScheme scheme = new TerminalColorScheme();
        RowLayout layout = new RowLayout();
        for (int script = 0; script < TerminalBenchmarkScripts.Names.Length; script++)
        {
            string name = TerminalBenchmarkScripts.Names[script];
            List<string> chunks = TerminalBenchmarkScripts.Create(script, 100, 30);
            List<string> again = TerminalBenchmarkScripts.Create(script, 100, 30);
            Check(chunks.Count > 0 && chunks.SequenceEqual(again), name + ": the same chunks on every run");
            TerminalDisplayModel model = new TerminalDisplayModel(100, 30) { ScrollbackLimit = 2000 };
            for (int index = 0; index < chunks.Count; index++)
            {
                if (index == chunks.Count - 1 && script == 2)
                {
                    // The redraw script draws on the alternate screen and leaves it with its last chunk.
                    Check(model.ActiveBufferIndex == 1 && model.CaretHidden && model.Document.GetLineText(1).Contains("|"), name + ": draws on the alternate screen");
                }
                model.ProcessOutput(chunks[index]);
            }
            int runs = 0;
            for (int row = 0; row < model.Rows; row++)
            {
                int line = model.TopLine + row;
                string text = line < model.Document.LineCount ? model.Document.GetLineText(line) : "";
                RowRunBuilder.Build(text, model.Lines.GetLine(line), scheme, model.Columns, layout);
                runs += layout.Texts.Count;
            }
            Check(model.ActiveBufferIndex == 0 && !model.CaretHidden && (runs > 0 || script == 2), name + ": ends on the normal screen" + (script == 2 ? "" : " with text on it"));
        }
    }

    // What the WPF view of a classic tab takes over from TerminalEmulatorControl: byte mode, the colours of a log, the links, Clear and a new session.
    private static void ClassicTerminalChecks()
    {
        TerminalColorScheme scheme = new TerminalColorScheme();
        RowLayout layout = new RowLayout();

        // Byte mode: a Cyrillic letter (two UTF-8 bytes) that the remote editor echoes one byte at a time.
        TerminalDisplayModel bytes = new TerminalDisplayModel(20, 3) { ByteOrientedLineEditing = true };
        char first = (char)(Utf8StreamDecoder.EscapedByteBase + 0xD0);
        char second = (char)(Utf8StreamDecoder.EscapedByteBase + 0xB6);
        bytes.ProcessOutput("$ a" + first);
        Equal(bytes.Document.GetLineText(0), "$ a\uFFFD", "byte mode: the first byte of a letter is a cell of its own");
        bytes.ProcessOutput(second.ToString());
        Equal(bytes.Document.GetLineText(0), "$ a\u0436", "byte mode: the second byte completes the letter in place");
        Equal(bytes.CursorPosition.X, 4, "byte mode: the cursor is after the letter");
        Equal(bytes.ByteKeyCount(VirtualKey.Back), 2, "byte mode: Backspace after the letter is sent once per byte");
        Equal(bytes.ByteKeyCount(VirtualKey.Back), 1, "byte mode: the next Backspace crosses an ASCII letter");
        bytes.DropPrediction();
        bytes.ProcessOutput("\b\b");
        Equal(bytes.CursorPosition.X, 3, "byte mode: two backspaces of the remote editor cross one letter of two bytes");
        bytes.ByteOrientedLineEditing = false;
        Equal(bytes.ByteKeyCount(VirtualKey.Back), 1, "without byte mode a key is sent once");
        bytes.ProcessOutput("x" + first);
        Check(bytes.Document.GetLineText(0).EndsWith("x\uFFFD"), "without byte mode an escaped byte is shown as U+FFFD");
        int flushes = 0;
        TerminalDisplayModel flushed = new TerminalDisplayModel(20, 3) { ByteOrientedLineEditing = true, FlushPendingOutput = () => flushes++ };
        flushed.ByteKeyCount(VirtualKey.Left);
        Equal(flushes, 1, "byte mode: the output that waits is parsed before the keys are counted");
        flushed.ProcessLocalOutput("ab\b");
        Equal(flushed.CursorPosition.X, 1, "a local echo steps back one character");

        // The colours of a log.
        TerminalDisplayModel log = new TerminalDisplayModel(40, 4) { HighlightLogLevels = true };
        log.ProcessOutput("12:00 ERROR disk full\r\n12:01 info ok\r\n");
        RowRunBuilder.Build(log.Document.GetLineText(0), log.Lines.GetLine(0), scheme, 40, layout);
        Check(layout.Texts.Exists(run => run.Foreground.Argb == 0xFFFF5555), "a log: an ERROR line is bright red");

        // Links.
        LinkHost host = new LinkHost();
        TerminalDisplayModel links = new TerminalDisplayModel(60, 4) { UrlLinks = true, FileLinks = true, LinkHost = host };
        links.ProcessOutput("see https://example.com/page now\r\n/src/main.c:12: error\r\n");
        ILinkTarget url = links.GetLinkAt(0, 6);
        Check(url != null && url.IsActive, "an address in the output is an active link");
        url.Activate();
        Equal(host.OpenedUrl, "https://example.com/page", "a click opens the address");
        Check(links.GetLinkAt(0, 1) == null, "a word is no link");
        ILinkTarget file = links.GetLinkAt(1, 3);
        Check(file != null, "a file name with a known extension is a link");
        file?.Activate();
        Equal(host.OpenedFile, "/src/main.c:11", "a click opens the file at its line (counted from 0)");
        links.ProcessOutput("\u001b[?1000h");
        Check(!links.LinksAvailable, "no links while the program reads the mouse");
        links.ProcessOutput("\u001b[?1000l\u001b[?1049h");
        Check(!links.LinksAvailable, "no links on the alternate screen");
        links.ProcessOutput("\u001b[?1049l");
        Check(links.LinksAvailable, "links again on the normal screen");

        // Clear and a new session.
        TerminalDisplayModel clear = new TerminalDisplayModel(20, 3);
        clear.ProcessOutput("one\r\ntwo\r\nthree\r\n$ ");
        clear.ClearOutput();
        Equal(clear.Document.GetLineText(0) + "|" + clear.CursorPosition.Y + "|" + clear.TopLine, "$ |0|0", "Clear: the line of the cursor goes to the top");
        clear.ProcessOutput("\u001b[?1049hfull");
        clear.ClearOutput();
        Equal(clear.ActiveBufferIndex, 1, "Clear leaves the alternate screen alone");
        clear.ProcessOutput("\u001b[?1049l");

        TerminalDisplayModel session = new TerminalDisplayModel(20, 3);
        session.ProcessOutput("old output\r\n$ x\u001b[?1000h\u001b(0");
        session.BeginNewSession();
        Check(session.MouseMode == MouseReportingMode.Off, "a new session: the mouse of the old program is off");
        Equal(session.CursorPosition.X, 0, "a new session starts at the start of a row");
        RowRunBuilder.Build(session.Document.GetLineText(0), session.Lines.GetLine(0), scheme, 20, layout);
        Check(layout.Texts.Count == 1 && layout.Texts[0].Foreground == scheme.Inactive, "a new session: the old output is dimmed");
        session.ProcessOutput("q");
        Check(session.Document.GetLineText(session.CursorDocumentLine).StartsWith("q"), "a new session: the line drawing set is off");
        Equal(session.GetScreenLineText(session.CursorPosition.Y), session.Document.GetLineText(session.CursorDocumentLine), "the text of a row of the screen");
        session.AutoWrap = false;
        Check(!session.AutoWrap, "auto-wrap can be turned off");
    }

    // The search bar of the window in the document of a WPF terminal.
    private static void SearchChecks()
    {
        TerminalDisplayModel model = new TerminalDisplayModel(30, 5);
        model.ProcessOutput("alpha beta\r\ngamma alpha\r\ndelta\r\n");
        TextMatcher alpha = TextMatcher.Literal("alpha", ignoreCase: true);
        TextDocument.TextPosition none = TextDocument.TextPosition.Invalid;
        TextDocument.TextPosition found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(0, 0), alpha, false, true, none, none, out int length);
        Equal(found.Line + ":" + found.Column + "+" + length, "0:0+5", "the first match");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(1, 0), alpha, false, true, none, none, out length);
        Equal(found.Line + ":" + found.Column, "1:6", "the next match after the caret");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(7, 1), alpha, false, true, none, none, out length);
        Equal(found.Line + ":" + found.Column, "0:0", "the search wraps around");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(7, 1), alpha, false, false, none, none, out length);
        Check(!found.IsValid, "without wrap-around nothing after the last match");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(5, 1), alpha, true, true, none, none, out length);
        Equal(found.Line + ":" + found.Column, "0:0", "backwards");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(0, 0), alpha, false, true, new TextDocument.TextPosition(0, 1), new TextDocument.TextPosition(11, 1), out length);
        Equal(found.Line + ":" + found.Column, "1:6", "inside a scope");
        found = DocumentSearch.Find(model.Document, new TextDocument.TextPosition(0, 0), TextMatcher.Literal("zeta", true), false, true, none, none, out length);
        Check(!found.IsValid, "no match");
    }

    // The session reads the Tag of a tab's terminal on the thread of the channel and the window on the thread of the input. A WPF element's own
    // Tag is a dependency property that throws there: with it no output was shown and no key reached the shell. The WPF terminal must hide it
    // with a field of its own (the WPF view cannot run on Linux, so its source is checked).
    private static void ThreadingSourceChecks()
    {
        string directory = AppContext.BaseDirectory;
        while (directory != null && !System.IO.File.Exists(System.IO.Path.Combine(directory, "SmarTTY.sln")))
        {
            directory = System.IO.Path.GetDirectoryName(directory);
        }
        if (directory == null)
        {
            Console.WriteLine("SKIP: the repository was not found, the source checks are not run");
            return;
        }
        string terminal = System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "GUI", "TerminalView", "WpfTerminal.cs"));
        Check(terminal.Contains("public new object Tag"), "the WPF terminal hides the dependency property Tag with one that any thread may read");
        Check(terminal.Contains("Volatile.Read(ref _tag)"), "its Tag is a plain field");
    }

    // The command line of the Smart Terminal on the WPF view (Terminal/Rendering/PromptLine).
    private static void PromptLineChecks()
    {
        TerminalDisplayModel model = new TerminalDisplayModel(10, 4);
        model.ProcessOutput("Welcome");
        PromptLine line = new PromptLine(model, model.Document.LineCount, "$ ");
        Equal(model.Document.GetLineText(1), "$ ", "the prompt is a line of its own at the end");
        Equal(model.Caret.Line + ":" + model.Caret.Column, "1:2", "the caret is after the prompt");
        int changes = 0;
        string lastChange = "";
        line.TextChanged += (start, removed, added) =>
        {
            changes++;
            lastChange = start + "," + removed + "," + added;
        };
        line.Insert("ls -la /tmp");
        Equal(model.Document.GetLineText(1) + "|" + model.Document.GetLineText(2), "$ ls -la /|tmp", "a long line wraps at the width of the screen");
        Equal(line.Text, "ls -la /tmp", "the text without the prompt");
        Equal(model.Caret.Line + ":" + model.Caret.Column, "2:3", "the caret at the end of the text, on the second row");
        Equal(lastChange + " " + changes, "0,0,11 1", "the change is told (where, removed, added)");
        line.MoveCaret(line.StepIndex(forward: false, word: true), extend: false);
        Equal(line.CaretIndex, 8, "Ctrl+Left to the start of the word (the path is split at '/')");
        line.MoveCaret(0, extend: false);
        line.MoveCaret(line.StepIndex(forward: true, word: true), extend: false);
        Equal(line.CaretIndex, 2, "Ctrl+Right to the end of the word");
        line.MoveCaret(line.Text.Length, extend: false);
        line.DeleteBackward(word: false);
        Equal(line.Text, "ls -la /tm", "Backspace");
        line.DeleteBackward(word: true);
        Equal(line.Text, "ls -la /", "Ctrl+Backspace deletes the word before the caret");
        line.MoveCaret(0, extend: false);
        line.DeleteForward(word: false);
        Equal(line.Text, "s -la /", "Delete");
        line.MoveCaret(2, extend: false);
        line.MoveCaret(5, extend: true);
        Check(line.HasSelection && line.SelectedText == "-la", "Shift moves select text of the line");
        Check(model.HasSelection && model.GetSelectedText() == "-la", "the selection is shown in the view");
        line.Insert("-l");
        Equal(line.Text + "|" + line.CaretIndex, "s -l /|4", "typing replaces the selection");
        Check(!model.HasSelection, "and the selection is gone");
        line.SelectAll();
        Equal(line.SelectedText, "s -l /", "Ctrl+A selects the text, not the prompt");
        line.SetText("echo \u00e9\uD83D\uDE00");
        line.DeleteBackward(word: false);
        Equal(line.Text, "echo \u00e9", "a surrogate pair is deleted whole");
        line.MoveCaret(line.StepIndex(forward: false, word: false), extend: false);
        Equal(line.CaretIndex, 5, "Left steps over one character");
        line.SetText("a\r\nb");
        Equal(line.Text, "ab", "the line breaks of a pasted text are not part of the line");
        line.SetText("12345678901234", 3);
        Equal(line.CaretIndex, 3, "SetText puts the caret where asked");
        Equal(model.Document.LineCount, 3, "the rows of the line: 16 characters on rows of 10");
        Equal(line.IndexOf(model.Document.EndPosition), 14, "the end of the document is the end of the text");
        Equal(line.IndexOf(new TextDocument.TextPosition(1, 1)), 0, "a place in the prompt is the start of the text");
        Equal(line.IndexOf(new TextDocument.TextPosition(2, 0)), 0, "a place in the output above is the start of the text");
        Check(line.Contains(new TextDocument.TextPosition(4, 2)) && !line.Contains(new TextDocument.TextPosition(0, 0)), "what is in the line and what is not");
        Equal(line.IndexOf(new TextDocument.TextPosition(4, 2)), 12, "a place on the second row");
        Check(line.IsAtWordStart == false, "the caret is inside a word");
        line.SetText("ls ", -1);
        Check(line.IsAtWordStart, "after a blank the caret is at the start of a word");

        model.Resize(20, 4);
        line.SetText("ls -la /tmp");
        Equal(model.Document.GetLineText(1), "$ ls -la /tmp", "after a resize the line is wrapped at the new width");
        Equal(model.Document.LineCount, 2, "one row now");

        // A command runs: its output starts on a new line after the command line, the caret is the cursor again.
        model.BeginOutputAtEnd();
        Equal(model.ScreenTopLine, 2, "the screen of the command starts after the command line");
        model.ProcessOutput("out1\r\nout2\r\n");
        Equal(model.Document.GetLineText(2) + "|" + model.Document.GetLineText(3), "out1|out2", "the output of the command");
        Equal(model.Caret.Line, 4, "the caret follows the output");
        Equal(model.Document.GetLineText(1), "$ ls -la /tmp", "the command line stays above");
        PromptLine next = new PromptLine(model, model.Document.LineCount, "$ ");
        Equal(model.Caret.Line + ":" + model.Caret.Column, "5:2", "the next prompt after the output");
        model.TopLine = 0;
        model.ScrollToScreen();
        Check(model.Caret.Line >= model.TopLine && model.Caret.Line < model.TopLine + model.Rows, "a key brings the command line into the view");
        model.Reset();
        model.ProcessOutput("x");
        Equal(model.Caret.Line + ":" + model.Caret.Column, "0:1", "a reset gives the caret back to the screen");
        Check(next.Prompt == "$ ", "the prompt");
    }
}
