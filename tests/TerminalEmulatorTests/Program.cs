using System;
using System.Collections.Generic;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;

// Checks of what the escape sequences do (Terminal/Emulation/TerminalEmulator.cs), with a fake screen and a fake host that write down the calls
// the emulator makes on them. The Mono stand (tests/TerminalSnapshots) checks the same through the real control; these say it directly.
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

    private sealed class Screen : ITerminalScreen
    {
        public readonly List<string> Log = new List<string>();

        private GridPoint _cursor;

        public GridPoint Cursor
        {
            get => _cursor;
            set
            {
                // The control brings the cursor back to the screen.
                _cursor = new GridPoint(Math.Max(0, Math.Min(value.X, Columns)), Math.Max(0, Math.Min(value.Y, Rows - 1)));
            }
        }

        public int Rows { get; set; } = 24;

        public int Columns { get; set; } = 80;

        public TabStops Tabs { get; } = new TabStops();

        public CellStyle CurrentStyle { get; set; }

        public int ActiveBufferIndex
        {
            get => _buffer;
            set
            {
                _buffer = value;
                Log.Add("buffer " + value);
            }
        }

        private int _buffer;

        public bool HasScrollRegion { get; set; }

        public int ScrollTop { get; set; }

        public int ScrollBottom { get; set; } = 23;

        public bool InsertMode
        {
            set => Log.Add("insert " + value);
        }

        public bool HideCaret
        {
            set => Log.Add("hide " + value);
        }

        public void MoveCursorBy(int deltaX, int deltaY, bool scroll = false)
        {
            Log.Add("move " + deltaX + "," + deltaY + (scroll ? " scroll" : ""));
            Cursor = new GridPoint(_cursor.X + deltaX, _cursor.Y + deltaY);
        }

        public void InsertChars(int count) => Log.Add("insertchars " + count);

        public void DeleteChars(int count) => Log.Add("deletechars " + count);

        public void EraseInDisplay(EraseMode eraseMode) => Log.Add("erase display " + eraseMode);

        public void EraseInLine(EraseMode eraseMode) => Log.Add("erase line " + eraseMode);

        public void InsertBlankLines(int line, int count) => Log.Add("insertlines " + line + "," + count);

        public void ScrollRegionUp(int topLine, int count) => Log.Add("scrollup " + topLine + "," + count);

        public void WriteAt(GridPoint point, string text, CellStyle cellStyle) => Log.Add("write " + point.X + "," + point.Y + " '" + text + "'");

        public void RepeatPreviousChar(int count) => Log.Add("repeat " + count);

        public void SetScrollRegion(int top, int bottom) => Log.Add("region " + top + ".." + bottom);

        public void ClearScrollRegion() => Log.Add("region cleared");

        public void ClearActiveScreen() => Log.Add("screen cleared");

        public void InvalidateView() => Log.Add("invalidate");
    }

    private sealed class Host : ITerminalHost
    {
        public readonly List<string> Replies = new List<string>();

        public readonly List<string> Log = new List<string>();

        public bool Bytes;

        public int ByteOffsetSet = -1;

        public int TabWidth { get; set; } = 8;

        public bool ByteColumnsActive => Bytes;

        public int ByteOffset
        {
            set => ByteOffsetSet = value;
        }

        public MouseReportingMode Mouse;

        public string TitleSet;

        public string DirectorySet;

        public void SendReply(string reply) => Replies.Add(reply);

        public int ByteStateResets;

        public int StreamResets;

        public void ResetByteState() => ByteStateResets++;

        public void ResetFromStream() => StreamResets++;

        public void MoveForwardBytes(int bytes) => Log.Add("bytes forward " + bytes);

        public void MoveBackBytes(int bytes) => Log.Add("bytes back " + bytes);

        MouseReportingMode ITerminalHost.MouseMode
        {
            set => Mouse = value;
        }

        string ITerminalHost.Title
        {
            set => TitleSet = value;
        }

        string ITerminalHost.ReportedDirectory
        {
            set => DirectorySet = value;
        }
    }

    private sealed class Rig
    {
        public readonly Screen Screen = new Screen();

        public readonly Host Host = new Host();

        public readonly TerminalModes Modes = new TerminalModes();

        public readonly TerminalEmulator Emulator;

        public Rig()
        {
            Emulator = new TerminalEmulator(Screen, Host, Modes);
        }

        public bool Csi(char final, string parameters = "", bool set = false)
        {
            Screen.Log.Clear();
            return Emulator.ExecuteCsi(final, parameters.Length == 0 ? new string[0] : parameters.Split(';'), set);
        }

        public string Log => string.Join("; ", Screen.Log);

        // Scans the sequence that starts at the position; returns the number of characters it takes.
        public int Scan(string text, int position = 0)
        {
            Screen.Log.Clear();
            return Emulator.ParseEscapeSequence(text, position, out _);
        }
    }

    private static void Main()
    {
        Cursor();
        Erasing();
        ScrollRegion();
        Modes();
        Replies();
        Style();
        Osc();
        Scanner();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " terminal emulator checks passed.");
    }

    private static void Cursor()
    {
        Rig rig = new Rig();
        rig.Csi('A', "5");
        Equal(rig.Log, "move 0,-5", "CSI 5 A goes up five rows");
        rig.Csi('B');
        Equal(rig.Log, "move 0,1", "CSI B without a count goes down one");
        rig.Csi('B', "0");
        Equal(rig.Log, "move 0,1", "...and a count of 0 is one too");
        rig.Csi('C', "3");
        Equal(rig.Log, "move 3,0", "CSI 3 C goes right");
        rig.Csi('D', "3");
        Equal(rig.Log, "move -3,0", "CSI 3 D goes left");
        rig.Host.Bytes = true;
        rig.Csi('C', "2");
        Check(rig.Screen.Log.Count == 0 && rig.Host.Log[rig.Host.Log.Count - 1] == "bytes forward 2", "in byte mode right goes by bytes");
        rig.Csi('D', "2");
        Check(rig.Screen.Log.Count == 0 && rig.Host.Log[rig.Host.Log.Count - 1] == "bytes back 2", "...and left too");
        rig.Host.Bytes = false;

        rig.Screen.Cursor = new GridPoint(10, 5);
        rig.Csi('E', "2");
        Equal(rig.Log, "move -10,2", "CSI 2 E goes to the start of the row two below");
        rig.Screen.Cursor = new GridPoint(10, 5);
        rig.Csi('F', "2");
        Equal(rig.Log, "move -10,-2", "CSI 2 F goes to the start of the row two above");
        rig.Screen.Cursor = new GridPoint(10, 5);
        rig.Csi('G', "20");
        Equal(rig.Screen.Cursor, new GridPoint(19, 5), "CSI 20 G is column 20 (counted from 1)");
        rig.Csi('`', "7");
        Equal(rig.Screen.Cursor, new GridPoint(6, 5), "CSI 7 ` is the same");
        rig.Csi('d', "9");
        Equal(rig.Screen.Cursor, new GridPoint(6, 8), "CSI 9 d is row 9");
        rig.Csi('H', "3;4");
        Equal(rig.Screen.Cursor, new GridPoint(3, 2), "CSI 3 ; 4 H is row 3, column 4");
        rig.Csi('H');
        Equal(rig.Screen.Cursor, new GridPoint(0, 0), "CSI H is home");
        rig.Csi('f', ";20");
        Equal(rig.Screen.Cursor, new GridPoint(19, 0), "CSI ; 20 f keeps the row at 1");
        rig.Csi('H', "99;999");
        Equal(rig.Screen.Cursor, new GridPoint(80, 23), "a position past the screen is brought back to it by the screen");

        Rig tabs = new Rig();
        tabs.Screen.Cursor = new GridPoint(3, 0);
        tabs.Csi('I');
        Equal(tabs.Log, "move 5,0", "CSI I goes to the next tab (8 columns)");
        tabs.Host.TabWidth = 4;
        tabs.Screen.Cursor = new GridPoint(3, 0);
        tabs.Csi('I', "2");
        Equal(tabs.Log, "move 1,0; move 4,0", "CSI 2 I goes through two tabs of the width the host says");
        tabs.Screen.Tabs.SetStop(5);
        tabs.Screen.Tabs.SetStop(10);
        tabs.Screen.Cursor = new GridPoint(8, 0);
        tabs.Csi('Z');
        Equal(tabs.Log, "move -3,0", "CSI Z goes back to the previous tab stop");
        tabs.Csi('g', "3");
        tabs.Screen.Cursor = new GridPoint(8, 0);
        tabs.Csi('Z');
        Equal(tabs.Log, "move -8,0", "CSI 3 g clears the stops: back goes to the start");

        Rig limits = new Rig();
        limits.Csi('A', "2147483647");
        Equal(limits.Log, "move 0," + -CsiRepeatLimit.Clamp('A', int.MaxValue, 24, 80), "a huge count is cut to what the screen can use");
        limits.Screen.Rows = 10;
        limits.Csi('A', "2147483647");
        Equal(limits.Log, "move 0," + -CsiRepeatLimit.Clamp('A', int.MaxValue, 10, 80), "...by the size the screen has now");
    }

    private static void Erasing()
    {
        Rig rig = new Rig();
        rig.Csi('J');
        Equal(rig.Log, "erase display ToEnd; invalidate", "CSI J erases to the end of the screen");
        rig.Csi('J', "1");
        Equal(rig.Log, "erase display ToStart; invalidate", "CSI 1 J erases to the start");
        rig.Csi('J', "2");
        Equal(rig.Log, "erase display All; invalidate", "CSI 2 J erases it all");
        rig.Csi('J', "3");
        Equal(rig.Log, "invalidate", "CSI 3 J (and any other number) erases nothing but redraws");
        rig.Csi('K', "2");
        Equal(rig.Log, "erase line All; invalidate", "CSI 2 K erases the line");
        rig.Csi('K', "7");
        Equal(rig.Log, "invalidate", "...and a number it does not know erases nothing");

        rig.Csi('@', "3");
        Equal(rig.Log, "insertchars 3", "CSI 3 @ inserts characters");
        rig.Csi('P', "2");
        Equal(rig.Log, "deletechars 2", "CSI 2 P deletes characters");
        rig.Screen.Cursor = new GridPoint(70, 4);
        rig.Csi('X', "50");
        Equal(rig.Log, "write 70,4 '          '", "CSI 50 X erases to the end of the line only (10 cells), never onto the next line");
        rig.Screen.Cursor = new GridPoint(80, 4);
        rig.Csi('X', "5");
        Equal(rig.Log, "", "...and nothing at the end of the line");
        rig.Csi('b', "4");
        Equal(rig.Log, "repeat 4", "CSI 4 b repeats the last character");
        rig.Screen.Cursor = new GridPoint(0, 6);
        rig.Csi('L', "2");
        Equal(rig.Log, "insertlines 6,2", "CSI 2 L inserts lines at the cursor row");
        rig.Csi('M', "2");
        Equal(rig.Log, "scrollup 6,2", "CSI 2 M removes lines at the cursor row");
        rig.Screen.ScrollTop = 3;
        rig.Csi('S', "2");
        Equal(rig.Log, "scrollup 3,2", "CSI 2 S scrolls up from the top of the region");
        rig.Csi('T', "2");
        Equal(rig.Log, "insertlines 3,2", "CSI 2 T scrolls down from the top of the region");
    }

    private static void ScrollRegion()
    {
        Rig rig = new Rig();
        rig.Csi('r', "5;10");
        Equal(rig.Log, "region 4..9", "CSI 5 ; 10 r sets the region (counted from 0)");
        rig.Csi('r');
        Equal(rig.Log, "region cleared", "CSI r clears it");
        rig.Csi('r', "1;24");
        Equal(rig.Log, "region cleared", "...and so does the whole screen");
        rig.Csi('r', "0;99");
        Equal(rig.Log, "region cleared", "a 0 top and a bottom past the screen are the whole screen");
        rig.Csi('r', "10;5");
        Equal(rig.Log, "", "a top below the bottom is ignored");
        rig.Csi('r', ";5");
        Equal(rig.Log, "region 0..4", "an omitted top is the first row");
    }

    private static void Modes()
    {
        Rig rig = new Rig();
        rig.Csi('h', "1", set: true);
        Check(rig.Modes.ApplicationCursorKeys && rig.Modes.PrivateModes.IsSet(1), "CSI ? 1 h: application cursor keys, and the mode is recorded");
        rig.Csi('l', "1", set: true);
        Check(!rig.Modes.ApplicationCursorKeys && !rig.Modes.PrivateModes.IsSet(1), "CSI ? 1 l: normal again");
        rig.Csi('l', "25", set: true);
        Equal(rig.Log, "hide True", "CSI ? 25 l hides the caret");
        rig.Csi('h', "25", set: true);
        Equal(rig.Log, "hide False", "CSI ? 25 h shows it");
        rig.Csi('h', "4");
        Equal(rig.Log, "insert True", "CSI 4 h is the insert mode (ANSI, not private)");
        Check(!rig.Modes.PrivateModes.IsSet(4), "...and no private mode is recorded");
        rig.Csi('l', "4");
        Equal(rig.Log, "insert False", "CSI 4 l ends it");
        rig.Csi('h', "4", set: true);
        Equal(rig.Log, "", "CSI ? 4 h is another mode: not the insert mode");

        rig.Csi('h', "1000", set: true);
        Equal(rig.Host.Mouse, MouseReportingMode.Normal, "CSI ? 1000 h: the mouse is reported");
        rig.Csi('h', "1003", set: true);
        Equal(rig.Host.Mouse, MouseReportingMode.AnyEvent, "CSI ? 1003 h: all of its movements");
        rig.Csi('l', "1003", set: true);
        Equal(rig.Host.Mouse, MouseReportingMode.Off, "CSI ? 1003 l: no more");
        rig.Csi('h', "9", set: true);
        Equal(rig.Host.Mouse, MouseReportingMode.X10, "CSI ? 9 h: X10");
        rig.Csi('h', "2004", set: true);
        Check(rig.Modes.PrivateModes.IsSet(2004) && rig.Host.Mouse == MouseReportingMode.X10, "a mode the emulator does nothing about (2004) is only recorded");
        rig.Csi('h', "1;7;2004", set: true);
        Check(rig.Modes.ApplicationCursorKeys && rig.Modes.PrivateModes.IsSet(7), "several modes in one sequence");
        rig.Csi('h', "x;1049;", set: true);
        Check(rig.Modes.PrivateModes.IsSet(1049), "items that are no numbers are skipped");

        Rig alternate = new Rig();
        alternate.Screen.Cursor = new GridPoint(7, 3);
        alternate.Csi('h', "1049", set: true);
        Equal(alternate.Log, "buffer 1; screen cleared", "CSI ? 1049 h: the alternate screen, cleared");
        Equal(alternate.Screen.Cursor, new GridPoint(7, 3), "...with the cursor where it was");
        Equal(alternate.Modes.SavedCursor, new GridPoint(7, 3), "...and that cursor saved");
        alternate.Csi('l', "1049", set: true);
        Equal(alternate.Log, "buffer 0", "CSI ? 1049 l: back to the normal screen");
        alternate.Csi('l', "1049", set: true);
        Equal(alternate.Log, "", "...and nothing if it is already there");
        alternate.Csi('h', "47", set: true);
        Equal(alternate.Log, "buffer 1", "CSI ? 47 h: the alternate screen, not cleared");
        alternate.Csi('h', "1047", set: true);
        alternate.Csi('l', "1047", set: true);
        Equal(alternate.Log, "screen cleared; buffer 0", "CSI ? 1047 l clears the alternate screen when it leaves it");

        Rig save = new Rig();
        save.Screen.Cursor = new GridPoint(4, 2);
        save.Csi('h', "1048", set: true);
        save.Screen.Cursor = new GridPoint(9, 9);
        save.Csi('l', "1048", set: true);
        Equal(save.Screen.Cursor, new GridPoint(4, 2), "CSI ? 1048 h / l: save and restore the cursor");
        save.Screen.Cursor = new GridPoint(6, 6);
        save.Csi('s');
        save.Screen.Cursor = new GridPoint(1, 1);
        save.Csi('u');
        Equal(save.Screen.Cursor, new GridPoint(6, 6), "CSI s / CSI u: the same, the ANSI.SYS way");
        save.Csi('s', "1", set: true);
        Equal(save.Screen.Cursor, new GridPoint(6, 6), "CSI ? s saves modes, not the cursor");
        save.Csi('m', "1;31");
        save.Csi('s');
        save.Csi('m', "");
        Check(save.Screen.CurrentStyle.IsDefault, "the style is plain after CSI m");
        save.Csi('u');
        Check(save.Screen.CurrentStyle.Bold && save.Screen.CurrentStyle.Foreground == 2, "CSI u brings back the style that CSI s saved with the cursor");

        Rig remembered = new Rig();
        remembered.Csi('h', "1;2004", set: true);
        remembered.Csi('s', "1;2004", set: true);
        remembered.Csi('l', "1;2004", set: true);
        Check(!remembered.Modes.ApplicationCursorKeys && !remembered.Modes.PrivateModes.IsSet(2004), "modes are off after CSI ? l");
        remembered.Csi('r', "1;2004", set: true);
        Check(remembered.Modes.ApplicationCursorKeys && remembered.Modes.PrivateModes.IsSet(2004), "CSI ? r puts back the modes that CSI ? s saved (and does what setting them does)");
        remembered.Csi('r', "2004;1000", set: true);
        Check(remembered.Modes.PrivateModes.IsSet(2004) && remembered.Host.Mouse == MouseReportingMode.Off, "a mode that was never saved comes back off");

        Rig byteMode = new Rig();
        byteMode.Host.ByteOffsetSet = 5;
        byteMode.Csi('A');
        Equal(byteMode.Host.ByteOffsetSet, 0, "a command that moves the cursor puts the byte offset at the start of a cell");
        byteMode.Host.ByteOffsetSet = 5;
        byteMode.Csi('m');
        Equal(byteMode.Host.ByteOffsetSet, 5, "...but not the ones for colours, erasing, modes and replies");
        Check(!byteMode.Csi('y') && byteMode.Host.ByteOffsetSet == 0, "an unknown command is no command, and it resets the offset too");
    }

    private static void Scanner()
    {
        Rig rig = new Rig();
        Equal(rig.Scan("\u001b[5Ahello"), 4, "a whole CSI sequence takes its characters");
        Equal(rig.Log, "move 0,-5", "...and is carried out");
        Equal(rig.Scan("ab\u001b[2Jc", 2), 4, "from a position inside the text");
        Equal(rig.Log, "erase display All; invalidate", "...CSI 2 J");

        Equal(rig.Scan("\u001b[5"), 3, "a CSI sequence that has not ended takes the rest");
        Equal(rig.Modes.IncompleteEscape, "\u001b[5", "...and is kept for the next chunk");
        rig.Modes.IncompleteEscape = null;
        Equal(rig.Scan("ab\u001b", 2), 1, "an ESC at the very end takes itself");
        Equal(rig.Modes.IncompleteEscape, "\u001b", "...and is kept");
        rig.Modes.IncompleteEscape = null;
        Equal(rig.Scan("\u001b[31\nA"), 4, "a CSI cut short by a control character leaves that character to be dealt with");
        Equal(rig.Log, "", "...and carries out nothing");
        Equal(rig.Scan("\u001b[3\u0018A"), 4, "CAN cancels the sequence and belongs to it");
        Equal(rig.Scan("\u001b[31\u001b[0m"), 4, "an ESC cuts a CSI short and begins the next one");

        Equal(rig.Scan("\u001b]0;t\u0007x"), 6, "an OSC string ends at BEL");
        Equal(rig.Host.TitleSet, "t", "...and is carried out");
        Equal(rig.Scan("\u001b]2;u\u001b\\x"), 7, "or at ESC \\");
        Equal(rig.Host.TitleSet, "u", "...and is carried out too");
        rig.Host.TitleSet = "keep";
        Equal(rig.Scan("\u001b]0;never\u0018x"), 10, "CAN cancels an OSC string");
        Equal(rig.Scan("\u001b]0;never\u001a"), 10, "and so does SUB");
        Equal(rig.Scan("\u001b]0;ti\u001b[31m"), 6, "an ESC that is no terminator aborts it and begins the next sequence");
        Equal(rig.Host.TitleSet, "keep", "...without carrying it out");
        Equal(rig.Scan("\u001bPdata\u001b\\x"), 8, "a DCS string is skipped up to its terminator");
        Equal(rig.Scan("\u001b^pm\u0007"), 5, "PM too");
        Equal(rig.Scan("\u001b_apc\u0007"), 6, "APC too");
        Equal(rig.Scan("\u001bXsos\u0007"), 6, "SOS too");
        rig.Modes.IncompleteEscape = null;
        Equal(rig.Scan("\u001b]0;ti"), 6, "an OSC string that has not ended takes the rest");
        Equal(rig.Modes.IncompleteEscape, "\u001b]0;ti", "...and is kept");
        rig.Modes.IncompleteEscape = null;
        Equal(rig.Scan("\u001b]" + new string('a', 20000)), 1, "one that grows past the limit gives up its ESC, so that the rest is text");
        Check(rig.Modes.IncompleteEscape == null, "...and keeps nothing");

        Equal(rig.Scan("\u001b(0"), 3, "ESC ( 0 takes three characters");
        Equal(rig.Modes.Charsets.Translate("q"), "\u2500", "...and selects the line drawing set for G0");
        Equal(rig.Scan("\u001b(B"), 3, "ESC ( B");
        Equal(rig.Modes.Charsets.Translate("q"), "q", "...the plain set again");
        rig.Scan("\u001b)0");
        Equal(rig.Modes.Charsets.Translate("q"), "q", "ESC ) 0 designates G1, which is not shifted in");
        Equal(rig.Scan("\u001b#8"), 3, "ESC # 8 is taken and does nothing");
        Equal(rig.Scan("\u001b("), 2, "ESC ( at the end of the chunk takes what there is");
        Equal(rig.Modes.IncompleteEscape, "\u001b(", "...and keeps it");
        rig.Modes.IncompleteEscape = null;
        Equal(rig.Scan("\u001b(\u001b[31m"), 2, "ESC ( cut short by an ESC takes two characters and leaves the ESC");

        Equal(rig.Scan("\u001b="), 2, "ESC =");
        Check(rig.Modes.ApplicationKeypad, "...is the application keypad");
        rig.Scan("\u001b>");
        Check(!rig.Modes.ApplicationKeypad, "ESC > is the numeric keypad");

        rig.Screen.Cursor = new GridPoint(4, 2);
        rig.Host.ByteOffsetSet = 5;
        rig.Scan("\u001b7");
        Equal(rig.Host.ByteOffsetSet, 0, "an ESC sequence that is no CSI or OSC puts the byte offset at the start of a cell");
        rig.Screen.Cursor = new GridPoint(9, 9);
        rig.Scan("\u001b8");
        Equal(rig.Screen.Cursor, new GridPoint(4, 2), "ESC 7 / ESC 8 save and restore the cursor");
        rig.Host.ByteOffsetSet = 5;
        rig.Scan("\u001b[m");
        Equal(rig.Host.ByteOffsetSet, 5, "a CSI sequence leaves it to the command");

        rig.Screen.Cursor = new GridPoint(5, 0);
        rig.Scan("\u001bH");
        Equal(rig.Screen.Tabs.NextDistance(0, 8), 5, "ESC H sets a tab stop at the cursor");

        rig.Screen.Cursor = new GridPoint(3, 7);
        rig.Scan("\u001bD");
        Equal(rig.Log, "move 0,1 scroll", "ESC D moves down one row, scrolling at the bottom");
        rig.Screen.HasScrollRegion = true;
        rig.Screen.ScrollTop = 2;
        rig.Screen.ScrollBottom = 7;
        rig.Screen.Cursor = new GridPoint(3, 7);
        rig.Scan("\u001bD");
        Equal(rig.Log, "scrollup 2,1", "...and at the bottom of a region it scrolls the region");
        rig.Screen.Cursor = new GridPoint(3, 2);
        rig.Scan("\u001bM");
        Equal(rig.Log, "insertlines 2,1", "ESC M at the top of the region scrolls it down");
        rig.Screen.Cursor = new GridPoint(3, 5);
        rig.Scan("\u001bM");
        Equal(rig.Log, "move 0,-1 scroll", "...and elsewhere moves up one row");
        rig.Screen.Cursor = new GridPoint(6, 5);
        rig.Scan("\u001bE");
        Equal(rig.Log, "move -6,1 scroll", "ESC E goes to the start of the next row");

        Equal(rig.Scan("\u001bc"), 2, "ESC c takes two characters");
        Equal(rig.Host.StreamResets, 1, "...and asks the control for a full reset");
        Equal(rig.Scan("\u001bZ"), 2, "an ESC sequence that is not known takes two characters");
        Equal(rig.Scan("\u001b\u001b[1m"), 1, "ESC ESC takes the first ESC only");
        Equal(rig.Scan("\u001b\r"), 1, "ESC and a control character: the ESC only");

        Rig csi = new Rig();
        csi.Scan("\u001b[?1h");
        Check(csi.Modes.ApplicationCursorKeys, "CSI ? 1 h is carried out as a DEC mode");
        csi.Screen.Log.Clear();
        csi.Scan("\u001b[>4;2m");
        csi.Scan("\u001b[<u");
        csi.Scan("\u001b[=1c");
        Check(csi.Screen.CurrentStyle.IsDefault && csi.Host.Replies.Count == 0, "a private prefix other than ? is not the standard command (CSI > 4 ; 2 m is no SGR reset, = c is no attribute request)");
        csi.Scan("\u001b[1m");
        csi.Scan("\u001b[2 q");
        Check(csi.Screen.CurrentStyle.Bold && csi.Screen.Log.Count == 0, "intermediate bytes make another command: CSI 2 SP q is no scroll region and no erase");
        csi.Scan("\u001b[1\"p");
        Check(csi.Modes.ApplicationCursorKeys, "...and CSI 1 \" p is not a soft reset");

        csi.Modes.ApplicationKeypad = true;
        csi.Screen.Cursor = new GridPoint(7, 3);
        csi.Screen.HasScrollRegion = true;
        csi.Host.Mouse = MouseReportingMode.Normal;
        csi.Screen.Log.Clear();
        csi.Scan("\u001b[!p");
        Check(!csi.Modes.ApplicationCursorKeys && !csi.Modes.ApplicationKeypad, "CSI ! p (soft reset): the keys are normal");
        Check(csi.Screen.CurrentStyle.IsDefault && csi.Host.Mouse == MouseReportingMode.Off, "...the style is plain and the mouse is off");
        Equal(csi.Screen.Cursor, new GridPoint(7, 3), "...the cursor stays where it was");
        Check(csi.Screen.Log.Contains("region cleared") && csi.Screen.Log.Contains("insert False") && csi.Screen.Log.Contains("hide False"), "...the region is cleared, the insert mode and the caret are normal");
        Check(csi.Host.ByteStateResets >= 1, "...and the byte state is forgotten");
        csi.Modes.ApplicationCursorKeys = true;
        csi.Emulator.ResetModes();
        Check(!csi.Modes.ApplicationCursorKeys, "ResetModes does the same without the region and the cursor");
    }

    private static void Replies()
    {
        Rig rig = new Rig();
        rig.Csi('n', "5");
        Equal(rig.Host.Replies[0], "\u001b[0n", "CSI 5 n: the terminal is fine");
        rig.Screen.Cursor = new GridPoint(9, 4);
        rig.Csi('n', "6");
        Equal(rig.Host.Replies[1], "\u001b[5;10R", "CSI 6 n: the cursor position, counted from 1");
        rig.Screen.Cursor = new GridPoint(80, 0);
        rig.Csi('n', "6");
        Equal(rig.Host.Replies[2], "\u001b[1;80R", "...at the pending-wrap position the cursor is still on the last column");
        rig.Csi('n', "7");
        Equal(rig.Host.Replies.Count, 3, "another status request gets no reply");
        rig.Csi('n', "6", set: true);
        Equal(rig.Host.Replies.Count, 3, "CSI ? 6 n is a DEC request: no reply");
        rig.Csi('c');
        Equal(rig.Host.Replies[3], "\u001b[?1;2c", "CSI c: primary device attributes");
        rig.Csi('c', "0");
        Equal(rig.Host.Replies.Count, 5, "CSI 0 c too");
        rig.Csi('c', "1");
        rig.Csi('c', "", set: true);
        Equal(rig.Host.Replies.Count, 5, "...but no other count and no ? form");
    }

    private static void Style()
    {
        Rig rig = new Rig();
        rig.Csi('m', "1;31");
        Check(rig.Screen.CurrentStyle.Bold && rig.Screen.CurrentStyle.Foreground == 2, "CSI 1 ; 31 m: bold red");
        rig.Csi('m', "");
        Check(rig.Screen.CurrentStyle.IsDefault, "CSI m resets the style");
        rig.Csi('m', "31", set: true);
        Check(rig.Screen.CurrentStyle.IsDefault, "CSI ? 31 m is no colour");
        rig.Csi('m', "38;5;196");
        Equal(rig.Screen.CurrentStyle.ExtendedForeground, 197, "CSI 38 ; 5 ; n m: a palette colour");
        Check(rig.Csi('m', "0"), "an SGR sequence is a known command");
    }

    private static void Osc()
    {
        Rig rig = new Rig();
        Check(rig.Emulator.ExecuteOsc("0;my title", null) && rig.Host.TitleSet == "my title", "OSC 0 sets the title");
        Check(rig.Emulator.ExecuteOsc("2;a;b", null) && rig.Host.TitleSet == "a;b", "OSC 2 does too, with its semicolons");
        rig.Host.TitleSet = "keep";
        Check(!rig.Emulator.ExecuteOsc("1;icon", null) && rig.Host.TitleSet == "keep", "OSC 1 (the icon name) leaves the title");
        Check(rig.Emulator.ExecuteOsc("0;x\u0001y", null) && rig.Host.TitleSet == "xy", "control characters are taken out");
        Check(!rig.Emulator.ExecuteOsc("0", null) && !rig.Emulator.ExecuteOsc("x;y", null), "a string without a number and a semicolon is nothing");
        Check(rig.Emulator.ExecuteOsc("7;file://host/home/me/dir%20x", null) && rig.Host.DirectorySet == "/home/me/dir x", "OSC 7 reports the directory of the shell");
        rig.Host.DirectorySet = null;
        Check(!rig.Emulator.ExecuteOsc("7;nonsense", null) && rig.Host.DirectorySet == null, "...and ignores what is no directory");
    }
}
