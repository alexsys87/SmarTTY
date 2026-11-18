using SmarTTY.Controls;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Drives the real hex editor control (Controls/HexEditorControl.cs) with a script of keys and calls drawn from a seeded random generator, and writes down
// what it does after every step: the caret and the selection, the typing mode, the area, the bytes, the undo history, the answers and the events.
// HexEditorControlGolden.txt is what the code wrote before its editing logic was moved to TextEditing/HexEditingModel.cs; the answers must stay the same.
// The mouse, the clipboard and the page keys are not in the script: they need the layout (fonts) or the system clipboard.
//   xvfb-run -a mono tests/HexEditorControlTests/bin/Release/net48/HexEditorControlTests.exe            compares with the golden file
//   ... HexEditorControlTests.exe --record <file>                                                        writes the golden file (old code only)
internal static class Program
{
    // The control is sealed and takes its keys in the protected OnKeyDown and OnKeyPress: they are called as the framework calls them.
    private sealed class Driver
    {
        private static readonly MethodInfo KeyDownMethod = typeof(HexEditorControl).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo KeyPressMethod = typeof(HexEditorControl).GetMethod("OnKeyPress", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly HexEditorControl _control = new HexEditorControl();

        public HexEditorControl Control => _control;

        public void Down(Keys keyData)
        {
            KeyDownMethod.Invoke(_control, new object[] { new KeyEventArgs(keyData) });
        }

        public void Press(char key)
        {
            KeyPressMethod.Invoke(_control, new object[] { new KeyPressEventArgs(key) });
        }

        public void Dispose()
        {
            _control.Dispose();
        }
    }

    private static readonly List<string> Lines = new List<string>();

    private static Driver _driver;

    private static HexEditorControl _hex => _driver.Control;

    private static int _states;

    private static string _searchEvent = "-";

    private static string Hex(byte[] bytes, int max)
    {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < bytes.Length && i < max; i++)
        {
            text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.ToString() + (bytes.Length > max ? "..." : "");
    }

    // A short fingerprint of all the bytes (the golden file shows only the first ones).
    private static string Fnv(byte[] bytes)
    {
        uint hash = 2166136261;
        foreach (byte value in bytes)
        {
            hash = (hash ^ value) * 16777619;
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static void Record(string operation, string result)
    {
        HexHighlight highlight = _hex.HighlightAtCaret;
        byte[] bytes = _hex.Document.ToArray();
        Lines.Add(string.Join(" ", new[]
        {
            Lines.Count.ToString(CultureInfo.InvariantCulture) + ":" + operation,
            "ret=" + result,
            "caret=" + _hex.CaretOffset,
            "sel=" + (_hex.HasSelection ? _hex.SelectionStart + "-" + _hex.SelectionEnd : "none"),
            "len=" + _hex.Length,
            "ovw=" + (_hex.OverwriteMode ? 1 : 0),
            "ro=" + (_hex.ReadOnly ? 1 : 0),
            "ascii=" + (_hex.AsciiAreaActive ? 1 : 0),
            "mod=" + (_hex.IsModified ? 1 : 0),
            "undo=" + (_hex.CanUndo ? 1 : 0),
            "redo=" + (_hex.CanRedo ? 1 : 0),
            "states=" + _states,
            "search=" + _searchEvent,
            "hl=" + (highlight == null ? "-" : highlight.Start + "+" + highlight.Length + highlight.Label),
            "bpr=" + _hex.BytesPerRow,
            "bytes=" + Hex(bytes, 16) + " fnv=" + Fnv(bytes)
        }));
        _states = 0;
        _searchEvent = "-";
    }

    private static string Bool(bool value)
    {
        return value ? "T" : "F";
    }

    private static byte[] InitialBytes()
    {
        byte[] bytes = new byte[100];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 7 + 3) & 0xFF);
        }
        byte[] text = Encoding.ASCII.GetBytes("Hello, hex world! ABCABCABC hello HELLO");
        Array.Copy(text, 0, bytes, 20, text.Length);
        return bytes;
    }

    private static readonly Keys[] Arrows = { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End };

    private static readonly string HexDigits = "0123456789abcdefABCDEF";

    private static readonly string AsciiChars = "azAZ09 ~!éя";

    private static void Step(Random random)
    {
        int kind = random.Next(100);
        bool shift = random.Next(3) == 0;
        if (kind < 22)
        {
            Keys key = Arrows[random.Next(Arrows.Length)];
            Keys data = shift ? key | Keys.Shift : key;
            _driver.Down(data);
            Record("key " + data, "-");
        }
        else if (kind < 26)
        {
            Keys key = random.Next(2) == 0 ? Keys.Home : Keys.End;
            Keys data = Keys.Control | key | (shift ? Keys.Shift : Keys.None);
            _driver.Down(data);
            Record("key " + data, "-");
        }
        else if (kind < 44)
        {
            char digit = HexDigits[random.Next(HexDigits.Length)];
            _driver.Press(digit);
            Record("press " + digit, "-");
        }
        else if (kind < 50)
        {
            char key = AsciiChars[random.Next(AsciiChars.Length)];
            _driver.Press(key);
            Record("press U+" + ((int)key).ToString("X4", CultureInfo.InvariantCulture), "-");
        }
        else if (kind < 54)
        {
            Keys key = new[] { Keys.Back, Keys.Delete, Keys.Insert }[random.Next(3)];
            _driver.Down(key);
            Record("key " + key, "-");
        }
        else if (kind < 57)
        {
            _hex.AsciiAreaActive = !_hex.AsciiAreaActive;
            Record("toggle area", "-");
        }
        else if (kind < 60)
        {
            int offset = random.Next(-5, _hex.Length + 8);
            _hex.GoToOffset(offset);
            Record("go to " + offset, "-");
        }
        else if (kind < 62)
        {
            _hex.SelectAll();
            Record("select all", "-");
        }
        else if (kind < 64)
        {
            byte[] pattern = new byte[random.Next(1, 4)];
            random.NextBytes(pattern);
            bool done = _hex.FillSelection(pattern);
            Record("fill " + Hex(pattern, 8), Bool(done));
        }
        else if (kind < 66)
        {
            byte[] pattern = new byte[random.Next(1, 3)];
            random.NextBytes(pattern);
            int count = random.Next(-1, 9);
            bool done = _hex.InsertPattern(count, pattern);
            Record("insert pattern " + count + " " + Hex(pattern, 8), Bool(done));
        }
        else if (kind < 68)
        {
            int size = new[] { 2, 4, 8 }[random.Next(3)];
            bool done = _hex.SwapSelectionByteOrder(size, out string error);
            Record("swap " + size, Bool(done) + (error == null ? "" : "(" + error + ")"));
        }
        else if (kind < 71)
        {
            HexValueType type = (HexValueType)random.Next(0, 13);
            string[] texts = { "255", "-3", "4660", "0x1234", "1.5", "A", "1010", "100000", "zz", "" };
            string text = texts[random.Next(texts.Length)];
            bool done = _hex.WriteValue(type, text, out string error);
            Record("write " + type + " '" + text + "'", Bool(done) + (error == null ? "" : "(" + error + ")"));
        }
        else if (kind < 76)
        {
            Keys key;
            switch (random.Next(4))
            {
                case 0:
                    key = Keys.Control | Keys.Z;
                    break;
                case 1:
                    key = Keys.Control | Keys.Y;
                    break;
                case 2:
                    key = Keys.Control | Keys.Shift | Keys.Z;
                    break;
                default:
                    key = Keys.Control | Keys.A;
                    break;
            }
            _driver.Down(key);
            Record("key " + key, "-");
        }
        else if (kind < 78)
        {
            if (random.Next(2) == 0)
            {
                _hex.Undo();
                Record("undo", "-");
            }
            else
            {
                _hex.Redo();
                Record("redo", "-");
            }
        }
        else if (kind < 84)
        {
            byte[] bytes = _hex.Document.ToArray();
            byte[] pattern;
            if (bytes.Length > 4 && random.Next(3) != 0)
            {
                int start = random.Next(0, bytes.Length - 3);
                pattern = new byte[random.Next(1, 4)];
                Array.Copy(bytes, start, pattern, 0, pattern.Length);
            }
            else
            {
                pattern = new byte[] { (byte)random.Next(256), (byte)random.Next(256) };
            }
            bool forward = random.Next(2) == 0;
            bool ignoreCase = random.Next(2) == 0;
            bool wrap = random.Next(3) != 0;
            bool found = _hex.Search(pattern, forward, ignoreCase, wrap);
            Record("search " + Hex(pattern, 8) + (forward ? " fwd" : " back") + (ignoreCase ? " icase" : "") + (wrap ? " wrap" : ""), Bool(found) + "/" + _hex.LastSearchText);
        }
        else if (kind < 87)
        {
            Keys key = shift ? Keys.F3 | Keys.Shift : Keys.F3;
            _driver.Down(key);
            Record("key " + key, "-");
        }
        else if (kind < 90)
        {
            int action = random.Next(5);
            if (action == 0)
            {
                bool done = _hex.HighlightSelection(System.Drawing.Color.FromArgb(200, 50 + random.Next(100), 20), random.Next(2) == 0 ? "label " + random.Next(5) : "");
                Record("highlight", Bool(done));
            }
            else if (action == 1)
            {
                bool done = _hex.RemoveHighlight();
                Record("remove highlight", Bool(done));
            }
            else if (action == 2)
            {
                _hex.ClearHighlights();
                Record("clear highlights", "-");
            }
            else
            {
                Keys key = shift ? Keys.F2 | Keys.Shift : Keys.F2;
                _driver.Down(key);
                Record("key " + key, "-");
            }
        }
        else if (kind < 92)
        {
            _hex.ToggleOverwriteMode();
            Record("toggle overwrite", "-");
        }
        else if (kind < 95)
        {
            int bytesPerRow = HexEditorControl.BytesPerRowChoices[random.Next(3)];
            _hex.BytesPerRow = bytesPerRow;
            Record("bytes per row " + bytesPerRow, "-");
        }
        else if (kind < 97)
        {
            _hex.BigEndian = !_hex.BigEndian;
            Record("big endian " + _hex.BigEndian, "-");
        }
        else if (kind < 98)
        {
            _hex.ReadOnly = !_hex.ReadOnly;
            Record("read only", "-");
        }
        else if (kind < 99)
        {
            byte[] bytes = _hex.Document.ToArray();
            string description;
            byte[] checksumBytes = _hex.GetChecksumBytes(out description);
            Record("checksum bytes", Hex(checksumBytes, 16) + "/" + description);
        }
        else
        {
            int count = random.Next(0, 12);
            byte[] bytes = _hex.GetBytesAtCaret(count);
            Record("bytes at caret " + count, Hex(bytes, 16));
        }
    }

    private static void Run(int seed, int steps)
    {
        _driver = new Driver();
        _hex.Document = new HexDocument(InitialBytes(), InitialBytes());
        _hex.StateChanged += (sender, e) => _states++;
        _hex.SearchFinished += (sender, e) => _searchEvent = e.Found ? "found@" + e.Offset + "+" + e.Length : "none";
        _states = 0;
        _searchEvent = "-";
        Lines.Add("== seed " + seed);
        Random random = new Random(seed);
        for (int i = 0; i < steps; i++)
        {
            Step(random);
        }
        _driver.Dispose();
    }

    private static int Main(string[] args)
    {
        try
        {
            Run(1, 500);
            Run(2, 500);
            Run(3, 500);
            Run(20240601, 700);
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
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HexEditorControlGolden.txt");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] golden = File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int mismatches = 0;
        for (int i = 0; i < Math.Max(golden.Length, Lines.Count); i++)
        {
            string expected = i < golden.Length ? golden[i] : "(missing)";
            string actual = i < Lines.Count ? Lines[i] : "(missing)";
            if (expected != actual)
            {
                if (mismatches++ < 5)
                {
                    Console.WriteLine("FAIL: line " + (i + 1));
                    Console.WriteLine("  expected: " + expected);
                    Console.WriteLine("  actual:   " + actual);
                }
            }
        }
        if (mismatches > 0)
        {
            Console.WriteLine("FAIL: " + mismatches + " of " + Lines.Count + " lines differ from the golden file");
            return 1;
        }
        Console.WriteLine("PASS: all " + Lines.Count + " steps of the hex editor match the golden file");
        Console.WriteLine();
        Console.WriteLine("All hex editor control checks passed.");
        return 0;
    }
}
