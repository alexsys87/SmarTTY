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
// The mouse, the page keys, the wheel, the tool tip and the colours are in a run of their own on a shown control (RunView), whose points are
// found from the cells of the layout so that the fonts do not matter; the clipboard is not in it.
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

    // Runs that also look for the same pattern again and again from a caret that stands on a match, so that the edges of the search are met.
    private static bool _focusedSearches;

    private static void FocusedSearch(Random random)
    {
        byte[] bytes = _hex.Document.ToArray();
        if (bytes.Length < 4)
        {
            return;
        }
        int offset = random.Next(0, bytes.Length - 2);
        _hex.GoToOffset(offset);
        Record("go to " + offset, "-");
        byte[] pattern = new byte[random.Next(1, 4)];
        Array.Copy(bytes, offset, pattern, 0, pattern.Length);
        bool forward = random.Next(2) == 0;
        bool ignoreCase = random.Next(2) == 0;
        bool wrap = random.Next(4) != 0;
        for (int i = 0; i < 3; i++)
        {
            bool direction = i == 2 ? !forward : forward;
            bool found = _hex.Search(pattern, direction, ignoreCase, wrap);
            Record("search again " + Hex(pattern, 8) + (direction ? " fwd" : " back") + (ignoreCase ? " icase" : "") + (wrap ? " wrap" : ""), Bool(found) + "/" + _hex.LastSearchText);
        }
    }

    private static void Step(Random random)
    {
        int kind = random.Next(100);
        bool shift = random.Next(3) == 0;
        if (_focusedSearches && kind >= 78 && kind < 84)
        {
            FocusedSearch(random);
            return;
        }
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

    private static void Run(int seed, int steps, bool focusedSearches = false)
    {
        _focusedSearches = focusedSearches;
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

    // ---- The view: the layout, the mouse, the page keys, the wheel, the tool tip and the colours of the bytes (stage 3, step 3B-1: the net
    // before the geometry and the input of the control move to the model). The control is shown in a form whose height is chosen so that
    // exactly 12 rows are visible and that is wide enough for 16 bytes in a row, and every point is found from the cells of the layout, so
    // that what is written down does not depend on the fonts.

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(string name)
    {
        return (T)typeof(HexEditorControl).GetField(name, Private).GetValue(_hex);
    }

    private static T PrivateProperty<T>(string name)
    {
        return (T)typeof(HexEditorControl).GetProperty(name, Private).GetValue(_hex, null);
    }

    private static object CallPrivate(string name, params object[] arguments)
    {
        return typeof(HexEditorControl).GetMethod(name, Private).Invoke(_hex, arguments);
    }

    private static HexEditingModel Model => Field<HexEditingModel>("_model");

    private static string View()
    {
        return "first=" + Field<int>("_firstRow") + " scrollx=" + Field<int>("_scrollX") + " nib=" + Model.Nibble + " area=" + Model.Area;
    }

    // The middle of the cell of a byte: its hex digit (0 or 1) or its character, in a visible row.
    private static System.Drawing.Point Cell(int row, int column, bool ascii, int digit = 0)
    {
        int charWidth = Field<int>("_charWidth");
        int x = ascii ? Field<int>("_asciiStart") + column * charWidth : (int)CallPrivate("HexCellX", column) + digit * charWidth;
        x += charWidth / 2 - Field<int>("_scrollX");
        int y = Field<int>("_headerHeight") + (row - Field<int>("_firstRow")) * Field<int>("_rowHeight") + Field<int>("_rowHeight") / 2;
        return new System.Drawing.Point(x, y);
    }

    private static readonly MethodInfo MouseDownMethod = typeof(HexEditorControl).GetMethod("OnMouseDown", Private);

    private static readonly MethodInfo MouseMoveMethod = typeof(HexEditorControl).GetMethod("OnMouseMove", Private);

    private static readonly MethodInfo MouseUpMethod = typeof(HexEditorControl).GetMethod("OnMouseUp", Private);

    private static readonly MethodInfo MouseWheelMethod = typeof(HexEditorControl).GetMethod("OnMouseWheel", Private);

    private static void Mouse(MethodInfo method, MouseButtons button, System.Drawing.Point point, int delta = 0)
    {
        method.Invoke(_hex, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, delta) });
    }

    private static void RunView()
    {
        Lines.Add("== view");
        _driver = new Driver();
        Form form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(0, 0) };
        _hex.Dock = DockStyle.Fill;
        form.Controls.Add(_hex);
        byte[] bytes = new byte[1000];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 13 + 5) & 0xFF);
        }
        _hex.Document = new HexDocument(bytes, bytes);
        _hex.StateChanged += (sender, e) => _states++;
        form.ClientSize = new System.Drawing.Size(1400, 300);
        form.Show();
        Application.DoEvents();
        _hex.BytesPerRow = 16;
        _hex.GroupSize = 4;
        // The height that shows 12 rows, whatever the height of a row is.
        int height = 100;
        while (PrivateProperty<int>("VisibleRows") != 12 && height < 2000)
        {
            form.ClientSize = new System.Drawing.Size(1400, height);
            Application.DoEvents();
            if (PrivateProperty<int>("VisibleRows") < 12)
            {
                height += 1;
            }
            else
            {
                height -= 1;
            }
            if (PrivateProperty<int>("VisibleRows") > 12)
            {
                height -= 40;
            }
        }
        _states = 0;
        Record("layout visible=" + PrivateProperty<int>("VisibleRows") + " rows=" + PrivateProperty<int>("RowCount"), View());

        // The page keys move by the visible rows less one; the caret is kept in view.
        _driver.Down(Keys.PageDown);
        Record("key PageDown", View());
        _driver.Down(Keys.PageDown | Keys.Shift);
        Record("key Shift+PageDown", View());
        _driver.Down(Keys.PageUp);
        Record("key PageUp", View());
        _driver.Down(Keys.PageUp | Keys.Shift);
        Record("key Shift+PageUp", View());
        _driver.Down(Keys.PageUp);
        Record("key PageUp at the top", View());
        _driver.Down(Keys.Control | Keys.End);
        Record("key Ctrl+End", View());
        _driver.Down(Keys.PageDown);
        Record("key PageDown at the end", View());
        _driver.Down(Keys.Control | Keys.Home);
        Record("key Ctrl+Home", View());
        _hex.GoToOffset(500);
        Record("go to 500", View());
        _hex.GoToOffset(100);
        Record("go to 100 (above the first row)", View());
        _hex.GoToOffset(1000);
        Record("go to the end", View());
        _hex.GoToOffset(0);
        Record("go to 0", View());

        // Clicks: on the digits of a byte, in the gap after a group, on a character, on the header, left of the bytes, right of the characters,
        // below the last byte.
        Mouse(MouseDownMethod, MouseButtons.Left, Cell(2, 5, ascii: false, digit: 0));
        Mouse(MouseUpMethod, MouseButtons.Left, Cell(2, 5, ascii: false, digit: 0));
        Record("click row 2 byte 5 high digit", View());
        Mouse(MouseDownMethod, MouseButtons.Left, Cell(2, 5, ascii: false, digit: 1));
        Mouse(MouseUpMethod, MouseButtons.Left, Cell(2, 5, ascii: false, digit: 1));
        Record("click row 2 byte 5 low digit", View());
        System.Drawing.Point gap = Cell(3, 3, ascii: false, digit: 2);
        Mouse(MouseDownMethod, MouseButtons.Left, gap);
        Mouse(MouseUpMethod, MouseButtons.Left, gap);
        Record("click in the gap after the group of byte 3", View());
        Mouse(MouseDownMethod, MouseButtons.Left, Cell(4, 7, ascii: true));
        Mouse(MouseUpMethod, MouseButtons.Left, Cell(4, 7, ascii: true));
        Record("click row 4 character 7", View());
        Mouse(MouseDownMethod, MouseButtons.Left, new System.Drawing.Point(Cell(1, 1, ascii: false).X, 2));
        Record("click on the header", View());
        Mouse(MouseDownMethod, MouseButtons.Left, new System.Drawing.Point(2, Cell(5, 0, ascii: false).Y));
        Mouse(MouseUpMethod, MouseButtons.Left, new System.Drawing.Point(2, Cell(5, 0, ascii: false).Y));
        Record("click left of the bytes, row 5", View());
        System.Drawing.Point right = Cell(6, 15, ascii: true);
        right.X += 5 * Field<int>("_charWidth");
        Mouse(MouseDownMethod, MouseButtons.Left, right);
        Mouse(MouseUpMethod, MouseButtons.Left, right);
        Record("click right of the characters, row 6", View());

        // A drag selects, back to its start selects nothing, beyond the bottom of the table the selection ends at its edge without scrolling.
        Mouse(MouseDownMethod, MouseButtons.Left, Cell(1, 2, ascii: false));
        Mouse(MouseMoveMethod, MouseButtons.Left, Cell(3, 9, ascii: false, digit: 1));
        Record("drag from row 1 byte 2 to row 3 byte 9", View());
        Mouse(MouseMoveMethod, MouseButtons.Left, Cell(1, 2, ascii: false));
        Record("drag back to the start", View());
        Mouse(MouseMoveMethod, MouseButtons.Left, Cell(5, 4, ascii: true));
        Record("drag into the characters of row 5", View());
        System.Drawing.Point below = Cell(11, 8, ascii: false);
        below.Y += 20 * Field<int>("_rowHeight");
        Mouse(MouseMoveMethod, MouseButtons.Left, below);
        Record("drag below the table", View());
        Mouse(MouseUpMethod, MouseButtons.Left, below);
        Mouse(MouseMoveMethod, MouseButtons.None, Cell(2, 2, ascii: false));
        Record("move without a button after the drag", View());

        // The right button: inside the selection it keeps it, outside it moves the caret; the middle button does nothing.
        Mouse(MouseDownMethod, MouseButtons.Right, Cell(3, 0, ascii: true));
        Mouse(MouseUpMethod, MouseButtons.Right, Cell(3, 0, ascii: true));
        Record("right click in the selection", View());
        Mouse(MouseDownMethod, MouseButtons.Right, Cell(0, 3, ascii: false, digit: 1));
        Mouse(MouseUpMethod, MouseButtons.Right, Cell(0, 3, ascii: false, digit: 1));
        Record("right click outside", View());
        Mouse(MouseDownMethod, MouseButtons.Middle, Cell(0, 0, ascii: false));
        Record("middle click", View());

        // The byte under the mouse (for the tool tip): a digit, the gap, a character, right of the characters, past the end of the bytes, the header.
        _hex.GoToOffset(990);
        Record("go to 990", View());
        string hits = string.Join(",", new[]
        {
            CallPrivate("ByteAtPoint", Cell(61, 3, ascii: false, digit: 1)),
            CallPrivate("ByteAtPoint", Cell(61, 3, ascii: false, digit: 2)),
            CallPrivate("ByteAtPoint", Cell(61, 4, ascii: false)),
            CallPrivate("ByteAtPoint", Cell(61, 12, ascii: true)),
            CallPrivate("ByteAtPoint", new System.Drawing.Point(Cell(61, 15, ascii: true).X + 3 * Field<int>("_charWidth"), Cell(61, 15, ascii: true).Y)),
            CallPrivate("ByteAtPoint", Cell(62, 9, ascii: false)),
            CallPrivate("ByteAtPoint", new System.Drawing.Point(Cell(61, 1, ascii: false).X, 2))
        });
        Record("bytes under the mouse", hits);

        // The wheel: three rows a notch, the small steps of a touchpad add up, the first row stays in the content.
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(55, 0, ascii: false), 120);
        Record("wheel up a notch", View());
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(52, 0, ascii: false), 40);
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(52, 0, ascii: false), 40);
        Record("wheel up two thirds of a notch", View());
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(52, 0, ascii: false), 40);
        Record("wheel up the last third", View());
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(49, 0, ascii: false), -240);
        Record("wheel down two notches", View());
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(49, 0, ascii: false), -1200);
        Record("wheel down ten notches", View());
        _hex.GoToOffset(0);
        Mouse(MouseWheelMethod, MouseButtons.None, Cell(0, 0, ascii: false), 360);
        Record("wheel up at the top", View());

        // The tool tip of a byte: the offset, the value, the character, the word of its group in both byte orders, the coloured block, a change.
        _hex.GoToOffset(40);
        _hex.GroupSize = 1;
        Record("tip group 1", ((string)CallPrivate("DescribeByte", 41)).Replace("\n", "|"));
        _hex.GroupSize = 4;
        Record("tip group 4", ((string)CallPrivate("DescribeByte", 41)).Replace("\n", "|"));
        _hex.BigEndian = true;
        Record("tip group 4 big endian", ((string)CallPrivate("DescribeByte", 41)).Replace("\n", "|"));
        _hex.BigEndian = false;
        Record("tip of the last bytes (no whole group)", ((string)CallPrivate("DescribeByte", 998)).Replace("\n", "|"));
        _hex.GroupSize = 8;
        _hex.Document.ReplaceByte(66, 0x41);
        _hex.GoToOffset(64);
        _driver.Down(Keys.Right | Keys.Shift);
        _driver.Down(Keys.Right | Keys.Shift);
        _driver.Down(Keys.Right | Keys.Shift);
        _driver.Down(Keys.Right | Keys.Shift);
        _hex.HighlightSelection(System.Drawing.Color.FromArgb(10, 200, 30), "header");
        Record("tip of a changed byte in a block", ((string)CallPrivate("DescribeByte", 66)).Replace("\n", "|"));
        _hex.HighlightSelection(System.Drawing.Color.FromArgb(10, 200, 30), "");
        Record("tip of a block without a label", ((string)CallPrivate("DescribeByte", 65)).Replace("\n", "|"));

        // The colours of the bytes by kind, on a dark and a light background, and the same colour for all with the option off.
        foreach (bool colorByValue in new[] { true, false })
        {
            _hex.ColorByValue = colorByValue;
            foreach (System.Drawing.Color back in new[] { System.Drawing.Color.FromArgb(30, 30, 30), System.Drawing.Color.White })
            {
                _hex.BackColor = back;
                _hex.ForeColor = back.GetBrightness() < 0.5f ? System.Drawing.Color.FromArgb(220, 220, 220) : System.Drawing.Color.Black;
                List<string> colours = new List<string>();
                foreach (byte value in new byte[] { 0x00, 0xFF, 0x09, 0x1F, 0x7F, 0x20, 0x41, 0x7E, 0x80, 0xC3 })
                {
                    colours.Add(value.ToString("x2", CultureInfo.InvariantCulture) + ":" + ((System.Drawing.Color)CallPrivate("ByteTextColor", value)).ToArgb().ToString("x8", CultureInfo.InvariantCulture));
                }
                Record("colours " + (colorByValue ? "by value" : "plain") + " on " + back.ToArgb().ToString("x8", CultureInfo.InvariantCulture), string.Join(",", colours));
            }
        }
        form.Dispose();
    }

    private static int Main(string[] args)
    {
        try
        {
            Run(1, 500);
            Run(2, 500);
            Run(3, 500);
            Run(20240601, 700);
            Run(7, 600, focusedSearches: true);
            Run(8, 600, focusedSearches: true);
            RunView();
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
