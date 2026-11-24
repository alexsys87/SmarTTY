using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// The script of the hex editor stand (tests/HexEditorControlTests, which drove the WinForms HexEditorControl under Mono and was removed with it in
// stage 3E-5) run on the models of the hex editor (HexEditingModel, HexKeyMap, HexViewLayout, HexPointer, HexByteText, HexMenuModel,
// HexInspectorModel) through HexReplayView and HexReplayInspector, which do what the control and its inspector did around them, and compared with
// the golden file that the control wrote (HexEditorControlGolden.txt, copied as it was). The keys are given as VirtualKey (the same names and
// values as VirtualKey), the clicks at the points of the layout, the window sizes to the view. One field is not compared in the two runs that showed
// the control in a form (the view and the inspector): states=, the number of StateChanged events, which there also counted the focus events of the
// WinForms window.
internal static class Program
{
    private sealed class Driver
    {
        private readonly HexReplayView _control = new HexReplayView();

        public HexReplayView Control => _control;

        public void Down(VirtualKey keyData)
        {
            _control.KeyDown(keyData & VirtualKey.KeyCode, (keyData & VirtualKey.Control) != 0, (keyData & VirtualKey.Shift) != 0, (keyData & VirtualKey.Alt) != 0);
        }

        public void Press(char key)
        {
            _control.KeyPress(key);
        }

        public void Dispose()
        {
        }
    }

    private static readonly List<string> Lines = new List<string>();

    private static Driver _driver;

    private static HexReplayView _hex => _driver.Control;

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

    private static readonly VirtualKey[] Arrows = { VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down, VirtualKey.Home, VirtualKey.End };

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
            VirtualKey key = Arrows[random.Next(Arrows.Length)];
            VirtualKey data = shift ? key | VirtualKey.Shift : key;
            _driver.Down(data);
            Record("key " + data, "-");
        }
        else if (kind < 26)
        {
            VirtualKey key = random.Next(2) == 0 ? VirtualKey.Home : VirtualKey.End;
            VirtualKey data = VirtualKey.Control | key | (shift ? VirtualKey.Shift : VirtualKey.None);
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
            VirtualKey key = new[] { VirtualKey.Back, VirtualKey.Delete, VirtualKey.Insert }[random.Next(3)];
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
            VirtualKey key;
            switch (random.Next(4))
            {
                case 0:
                    key = VirtualKey.Control | VirtualKey.Z;
                    break;
                case 1:
                    key = VirtualKey.Control | VirtualKey.Y;
                    break;
                case 2:
                    key = VirtualKey.Control | VirtualKey.Shift | VirtualKey.Z;
                    break;
                default:
                    key = VirtualKey.Control | VirtualKey.A;
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
            VirtualKey key = shift ? VirtualKey.F3 | VirtualKey.Shift : VirtualKey.F3;
            _driver.Down(key);
            Record("key " + key, "-");
        }
        else if (kind < 90)
        {
            int action = random.Next(5);
            if (action == 0)
            {
                bool done = _hex.HighlightSelection(RgbColor.FromArgb(200, 50 + random.Next(100), 20), random.Next(2) == 0 ? "label " + random.Next(5) : "");
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
                VirtualKey key = shift ? VirtualKey.F2 | VirtualKey.Shift : VirtualKey.F2;
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
            int bytesPerRow = HexViewChoices.BytesPerRow[random.Next(3)];
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
    // before the geometry and the input of the control moved to the model). The view is shown in a window whose height is chosen so that
    // exactly 12 rows are visible and that is wide enough for 16 bytes in a row, and every point is found from the cells of the layout.

    private static HexEditingModel Model => _hex.Model;

    private static HexViewLayout Layout => _hex.Layout;

    private static string View()
    {
        return "first=" + Layout.FirstRow + " scrollx=" + Layout.ScrollX + " nib=" + Model.Nibble + " area=" + Model.Area;
    }

    private struct Point
    {
        public int X;

        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    // The middle of the cell of a byte: its hex digit (0 or 1) or its character, in a visible row.
    private static Point Cell(int row, int column, bool ascii, int digit = 0)
    {
        int charWidth = Layout.CharWidth;
        int x = ascii ? Layout.AsciiStart + column * charWidth : Layout.HexCellX(column) + digit * charWidth;
        x += charWidth / 2 - Layout.ScrollX;
        int y = Layout.HeaderHeight + (row - Layout.FirstRow) * Layout.RowHeight + Layout.RowHeight / 2;
        return new Point(x, y);
    }

    private static void Down(HexReplayButton button, Point point)
    {
        _hex.MouseDown(button, point.X, point.Y);
    }

    private static void Up(Point point)
    {
        _hex.MouseUp();
    }

    private static void Move(HexReplayButton button, Point point)
    {
        _hex.MouseMove(button, point.X, point.Y);
    }

    private static void Wheel(int delta)
    {
        _hex.MouseWheel(delta);
    }

    private static void RunView()
    {
        Lines.Add("== view");
        _driver = new Driver();
        byte[] bytes = new byte[1000];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 13 + 5) & 0xFF);
        }
        _hex.Document = new HexDocument(bytes, bytes);
        _hex.StateChanged += (sender, e) => _states++;
        _hex.Show(1400, 300);
        _hex.BytesPerRow = 16;
        _hex.GroupSize = 4;
        // The height that shows 12 rows, whatever the height of a row is.
        int height = 100;
        while (_hex.VisibleRows != 12 && height < 2000)
        {
            _hex.SetClientSize(1400, height);
            if (_hex.VisibleRows < 12)
            {
                height += 1;
            }
            else
            {
                height -= 1;
            }
            if (_hex.VisibleRows > 12)
            {
                height -= 40;
            }
        }
        _states = 0;
        Record("layout visible=" + _hex.VisibleRows + " rows=" + _hex.RowCount, View());

        // The page keys move by the visible rows less one; the caret is kept in view.
        _driver.Down(VirtualKey.PageDown);
        Record("key PageDown", View());
        _driver.Down(VirtualKey.PageDown | VirtualKey.Shift);
        Record("key Shift+PageDown", View());
        _driver.Down(VirtualKey.PageUp);
        Record("key PageUp", View());
        _driver.Down(VirtualKey.PageUp | VirtualKey.Shift);
        Record("key Shift+PageUp", View());
        _driver.Down(VirtualKey.PageUp);
        Record("key PageUp at the top", View());
        _driver.Down(VirtualKey.Control | VirtualKey.End);
        Record("key Ctrl+End", View());
        _driver.Down(VirtualKey.PageDown);
        Record("key PageDown at the end", View());
        _driver.Down(VirtualKey.Control | VirtualKey.Home);
        Record("key Ctrl+Home", View());
        _hex.GoToOffset(500);
        Record("go to 500", View());
        _hex.GoToOffset(100);
        Record("go to 100 (above the first row)", View());
        _hex.GoToOffset(1000);
        Record("go to the end", View());
        _hex.GoToOffset(0);
        Record("go to 0", View());

        // Clicks: on the digits of a byte, in the gap after a group, on a character, on the header, left of the bytes, right of the characters.
        Down(HexReplayButton.Left, Cell(2, 5, ascii: false, digit: 0));
        Up(Cell(2, 5, ascii: false, digit: 0));
        Record("click row 2 byte 5 high digit", View());
        Down(HexReplayButton.Left, Cell(2, 5, ascii: false, digit: 1));
        Up(Cell(2, 5, ascii: false, digit: 1));
        Record("click row 2 byte 5 low digit", View());
        Point gap = Cell(3, 3, ascii: false, digit: 2);
        Down(HexReplayButton.Left, gap);
        Up(gap);
        Record("click in the gap after the group of byte 3", View());
        Down(HexReplayButton.Left, Cell(4, 7, ascii: true));
        Up(Cell(4, 7, ascii: true));
        Record("click row 4 character 7", View());
        Down(HexReplayButton.Left, new Point(Cell(1, 1, ascii: false).X, 2));
        Record("click on the header", View());
        Down(HexReplayButton.Left, new Point(2, Cell(5, 0, ascii: false).Y));
        Up(new Point(2, Cell(5, 0, ascii: false).Y));
        Record("click left of the bytes, row 5", View());
        Point right = Cell(6, 15, ascii: true);
        right.X += 5 * Layout.CharWidth;
        Down(HexReplayButton.Left, right);
        Up(right);
        Record("click right of the characters, row 6", View());

        // A drag selects, back to its start selects nothing, beyond the bottom of the table the selection ends at its edge without scrolling.
        Down(HexReplayButton.Left, Cell(1, 2, ascii: false));
        Move(HexReplayButton.Left, Cell(3, 9, ascii: false, digit: 1));
        Record("drag from row 1 byte 2 to row 3 byte 9", View());
        Move(HexReplayButton.Left, Cell(1, 2, ascii: false));
        Record("drag back to the start", View());
        Move(HexReplayButton.Left, Cell(5, 4, ascii: true));
        Record("drag into the characters of row 5", View());
        Point below = Cell(11, 8, ascii: false);
        below.Y += 20 * Layout.RowHeight;
        Move(HexReplayButton.Left, below);
        Record("drag below the table", View());
        Up(below);
        Move(HexReplayButton.None, Cell(2, 2, ascii: false));
        Record("move without a button after the drag", View());

        // The right button: inside the selection it keeps it, outside it moves the caret; the middle button does nothing.
        Down(HexReplayButton.Right, Cell(3, 0, ascii: true));
        Up(Cell(3, 0, ascii: true));
        Record("right click in the selection", View());
        Down(HexReplayButton.Right, Cell(0, 3, ascii: false, digit: 1));
        Up(Cell(0, 3, ascii: false, digit: 1));
        Record("right click outside", View());
        Down(HexReplayButton.Middle, Cell(0, 0, ascii: false));
        Record("middle click", View());

        // The byte under the mouse (for the tool tip): a digit, the gap, a character, right of the characters, past the end of the bytes, the header.
        _hex.GoToOffset(990);
        Record("go to 990", View());
        Point[] points =
        {
            Cell(61, 3, ascii: false, digit: 1),
            Cell(61, 3, ascii: false, digit: 2),
            Cell(61, 4, ascii: false),
            Cell(61, 12, ascii: true),
            new Point(Cell(61, 15, ascii: true).X + 3 * Layout.CharWidth, Cell(61, 15, ascii: true).Y),
            Cell(62, 9, ascii: false),
            new Point(Cell(61, 1, ascii: false).X, 2)
        };
        List<string> hits = new List<string>();
        foreach (Point point in points)
        {
            hits.Add(_hex.ByteAtPoint(point.X, point.Y).ToString(CultureInfo.InvariantCulture));
        }
        Record("bytes under the mouse", string.Join(",", hits));

        // The wheel: three rows a notch, the small steps of a touchpad add up, the first row stays in the content.
        Wheel(120);
        Record("wheel up a notch", View());
        Wheel(40);
        Wheel(40);
        Record("wheel up two thirds of a notch", View());
        Wheel(40);
        Record("wheel up the last third", View());
        Wheel(-240);
        Record("wheel down two notches", View());
        Wheel(-1200);
        Record("wheel down ten notches", View());
        _hex.GoToOffset(0);
        Wheel(360);
        Record("wheel up at the top", View());

        // The tool tip of a byte: the offset, the value, the character, the word of its group in both byte orders, the coloured block, a change.
        _hex.GoToOffset(40);
        _hex.GroupSize = 1;
        Record("tip group 1", _hex.DescribeByte(41).Replace("\n", "|"));
        _hex.GroupSize = 4;
        Record("tip group 4", _hex.DescribeByte(41).Replace("\n", "|"));
        _hex.BigEndian = true;
        Record("tip group 4 big endian", _hex.DescribeByte(41).Replace("\n", "|"));
        _hex.BigEndian = false;
        Record("tip of the last bytes (no whole group)", _hex.DescribeByte(998).Replace("\n", "|"));
        _hex.GroupSize = 8;
        _hex.Document.ReplaceByte(66, 0x41);
        _hex.GoToOffset(64);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _hex.HighlightSelection(RgbColor.FromArgb(10, 200, 30), "header");
        Record("tip of a changed byte in a block", _hex.DescribeByte(66).Replace("\n", "|"));
        _hex.HighlightSelection(RgbColor.FromArgb(10, 200, 30), "");
        Record("tip of a block without a label", _hex.DescribeByte(65).Replace("\n", "|"));

        // The colours of the bytes by kind, on a dark and a light background, and the same colour for all with the option off.
        foreach (bool colorByValue in new[] { true, false })
        {
            _hex.ColorByValue = colorByValue;
            foreach (RgbColor back in new[] { RgbColor.FromArgb(30, 30, 30), RgbColor.FromArgb(255, 255, 255) })
            {
                _hex.SetBackColor(back);
                _hex.SetForeColor(HexByteText.IsDark(back) ? RgbColor.FromArgb(220, 220, 220) : RgbColor.FromArgb(0, 0, 0));
                List<string> colours = new List<string>();
                foreach (byte value in new byte[] { 0x00, 0xFF, 0x09, 0x1F, 0x7F, 0x20, 0x41, 0x7E, 0x80, 0xC3 })
                {
                    colours.Add(value.ToString("x2", CultureInfo.InvariantCulture) + ":" + _hex.ByteTextColor(value).ToArgb().ToString("x8", CultureInfo.InvariantCulture));
                }
                Record("colours " + (colorByValue ? "by value" : "plain") + " on " + back.ToArgb().ToString("x8", CultureInfo.InvariantCulture), string.Join(",", colours));
            }
        }
        // The form was disposed: the control lost the focus, and the StateChanged of that is counted in the next record (the first of the menu).
        _hex.Close();
    }

    // ---- Stage 3B-3: the submenus of the hex view and the data inspector ----
    // The menu is HexMenuModel with what HexEditorMenu did around it (the items with their text, key, swatch, enabled and checked state, the
    // submenus shown in the hex view only, the error of a command in a message box, the call after a command); the inspector is HexInspectorModel
    // with what HexDataInspector did (HexReplayInspector).

    private static string _dialogs = "";

    private static int _afterCommands;

    private static string _boxes = "";

    // HexEditorMenu without WinForms: the state of every item as the menu items held it.
    private sealed class ReplayMenu
    {
        public sealed class Item
        {
            public HexMenuItem Entry;

            public bool Enabled = true;

            public bool Checked;
        }

        public sealed class Submenu
        {
            public HexSubmenu Model;

            public bool Visible = true;

            public bool Enabled = true;

            public readonly List<Item> Items = new List<Item>();
        }

        public readonly List<Submenu> Menus = new List<Submenu>();

        private readonly Action _afterCommand;

        public ReplayMenu(IHexEditorView editor, Action<HexDialogKind> requestDialog, Action afterCommand)
        {
            _afterCommand = afterCommand;
            HexMenuModel model = new HexMenuModel(editor, requestDialog, error => _boxes += "[Asterisk:" + error + "]");
            foreach (HexSubmenu submenu in model.Submenus)
            {
                Submenu menu = new Submenu { Model = submenu };
                foreach (HexMenuItem entry in submenu.Items)
                {
                    menu.Items.Add(new Item { Entry = entry });
                }
                Menus.Add(menu);
            }
        }

        public void Refresh(bool hexMode)
        {
            foreach (Submenu menu in Menus)
            {
                menu.Visible = hexMode;
            }
            if (!hexMode)
            {
                return;
            }
            foreach (Submenu menu in Menus)
            {
                menu.Enabled = menu.Model.IsEnabled;
                foreach (Item item in menu.Items)
                {
                    if (!item.Entry.IsSeparator)
                    {
                        item.Enabled = item.Entry.IsEnabled;
                        item.Checked = item.Entry.IsChecked;
                    }
                }
            }
        }

        public void Click(string submenu, string text)
        {
            foreach (Submenu menu in Menus)
            {
                if (menu.Model.Text != submenu)
                {
                    continue;
                }
                foreach (Item item in menu.Items)
                {
                    if (!item.Entry.IsSeparator && item.Entry.Text == text)
                    {
                        try
                        {
                            item.Entry.Run();
                        }
                        catch (Exception exception)
                        {
                            _boxes += "[Hand:" + exception.Message + "]";
                        }
                        _afterCommand();
                        return;
                    }
                }
            }
            throw new InvalidOperationException("no menu item " + submenu + " > " + text);
        }
    }

    private static string MenuState(ReplayMenu menu)
    {
        List<string> parts = new List<string>();
        foreach (ReplayMenu.Submenu submenu in menu.Menus)
        {
            List<string> items = new List<string>();
            foreach (ReplayMenu.Item item in submenu.Items)
            {
                if (item.Entry.IsSeparator)
                {
                    items.Add("-");
                    continue;
                }
                items.Add(item.Entry.Text + "[" + (item.Enabled ? "E" : "") + (item.Checked ? "C" : "") + (item.Entry.Shortcut ?? "") + (item.Entry.Swatch.HasValue ? "I" : "") + "]");
            }
            parts.Add(submenu.Model.Text + (submenu.Visible ? "" : "(hidden)") + (submenu.Enabled ? "" : "(off)") + ":" + string.Join(",", items));
        }
        return string.Join(" | ", parts);
    }

    private static void Click(ReplayMenu menu, string submenu, string text)
    {
        menu.Click(submenu, text);
        menu.Refresh(hexMode: true);
        Record("menu " + submenu + " > " + text, "dialogs=" + _dialogs + " after=" + _afterCommands + " boxes=" + _boxes + " bigendian=" + Bool(_hex.BigEndian) + " group=" + _hex.GroupSize + " color=" + Bool(_hex.ColorByValue));
        _dialogs = "";
        _afterCommands = 0;
        _boxes = "";
    }

    private static void RunMenu()
    {
        Lines.Add("== menu");
        _driver = new Driver();
        _hex.StateChanged += (sender, e) => _states++;
        ReplayMenu menu = new ReplayMenu(_hex, kind => _dialogs += kind + ";", () => _afterCommands++);
        menu.Refresh(hexMode: false);
        Lines.Add("text view: " + MenuState(menu));
        _hex.Document = new HexDocument(new byte[0]);
        menu.Refresh(hexMode: true);
        Lines.Add("empty: " + MenuState(menu));
        _hex.Document = new HexDocument(InitialBytes(), InitialBytes());
        _hex.GoToOffset(20);
        menu.Refresh(hexMode: true);
        Lines.Add("caret: " + MenuState(menu));
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        _driver.Down(VirtualKey.Right | VirtualKey.Shift);
        menu.Refresh(hexMode: true);
        Lines.Add("selection: " + MenuState(menu));
        Click(menu, "Highlight", "Green");
        Lines.Add("highlighted: " + MenuState(menu));
        Click(menu, "Highlight", "Color and Label...");
        Click(menu, "Edit Bytes", "Fill Selection...");
        Click(menu, "Edit Bytes", "Insert Bytes...");
        Click(menu, "Edit Bytes", "Checksums and Hashes...");
        Click(menu, "Edit Bytes", "Swap Byte Order (16-bit Words)");
        Click(menu, "Edit Bytes", "Swap Byte Order (64-bit Words)");
        _hex.GoToOffset(50);
        Click(menu, "Highlight", "Yellow");
        _hex.GoToOffset(60);
        menu.Refresh(hexMode: true);
        Lines.Add("blocks, none at the caret: " + MenuState(menu));
        Click(menu, "Highlight", "Next Highlight");
        Click(menu, "Highlight", "Next Highlight");
        Click(menu, "Highlight", "Previous Highlight");
        Lines.Add("on a block: " + MenuState(menu));
        Click(menu, "Highlight", "Remove Highlight");
        Click(menu, "Highlight", "Remove All Highlights");
        Lines.Add("no blocks: " + MenuState(menu));
        Click(menu, "Hex View", "Big Endian (Network Order, MIPS, PowerPC)");
        Click(menu, "Hex View", "Group by 32-bit Words");
        Click(menu, "Hex View", "8 Bytes per Row");
        Lines.Add("8 in a row: " + MenuState(menu));
        Click(menu, "Hex View", "Color Bytes by Value");
        Click(menu, "Hex View", "Little Endian (x86, ARM)");
        Click(menu, "Hex View", "Single Bytes");
        Click(menu, "Hex View", "16 Bytes per Row");
        _hex.ReadOnly = true;
        menu.Refresh(hexMode: true);
        Lines.Add("read-only: " + MenuState(menu));
        _hex.ReadOnly = false;
        _hex.GoToOffset(_hex.Length);
        menu.Refresh(hexMode: true);
        Lines.Add("at the end: " + MenuState(menu));
        _driver.Dispose();
    }

    private static HexReplayInspector Inspector => _hex.Inspector;

    private static HexInspectorModel InspectorModel => Inspector.Model;

    private static void RecordInspector(string operation)
    {
        object editing = InspectorModel.Editing;
        Record("inspector " + operation, "editing=" + (editing == null ? "-" : editing.ToString()) + " box=" + (Inspector.EditVisible ? "[" + Inspector.EditText + "]" : "hidden")
            + " original=" + (InspectorModel.EditOriginalText ?? "-") + " row=" + InspectorModel.SelectedRow
            + " message=" + InspectorModel.Message + (InspectorModel.MessageIsError ? " (error)" : "")
            + " shown=" + Bool(_hex.ShowDataInspector) + " bigendian=" + Bool(_hex.BigEndian));
    }

    private static void RunInspector()
    {
        Lines.Add("== inspector");
        _driver = new Driver();
        byte[] bytes = new byte[64];
        // 0x0000: a float 1.5, then a time, then "é" and an "A".
        byte[] start = { 0x00, 0x00, 0xC0, 0x3F, 0x00, 0x2C, 0x5B, 0x65, 0xC3, 0xA9, 0x41, 0x80 };
        Array.Copy(start, bytes, start.Length);
        _hex.Document = new HexDocument(bytes, bytes);
        _hex.LoadViewOptions(16, 1, false, false, true);
        _hex.Show(1400, 400);
        IReadOnlyList<HexValueType> types = HexValueFormats.InspectorTypes;
        foreach (int offset in new[] { 0, 4, 8, 10, 11 })
        {
            _hex.GoToOffset(offset);
            for (int row = 0; row < types.Count; row++)
            {
                Inspector.BeginEdit(row);
                RecordInspector("edit " + types[row] + " at " + offset);
                Inspector.EditBoxKey(VirtualKey.Escape);
            }
        }
        _hex.GoToOffset(0);
        int floatRow = -1;
        int byteRow = -1;
        for (int row = 0; row < types.Count; row++)
        {
            if (types[row] == HexValueType.Float32)
            {
                floatRow = row;
            }
            if (types[row] == HexValueType.UInt8)
            {
                byteRow = row;
            }
        }
        Inspector.BeginEdit(floatRow);
        Inspector.EditBoxKey(VirtualKey.Enter);
        RecordInspector("Enter on the same text");
        Inspector.BeginEdit(floatRow);
        Inspector.EditText = "2.25";
        Inspector.EditBoxKey(VirtualKey.Enter);
        RecordInspector("Enter on 2.25");
        Inspector.BeginEdit(byteRow);
        Inspector.EditText = "300";
        Inspector.EditBoxKey(VirtualKey.Enter);
        RecordInspector("Enter on a byte of 300");
        Inspector.EditBoxKey(VirtualKey.Escape);
        RecordInspector("Esc");
        _hex.GoToOffset(1);
        Inspector.ShowMessage("x", false);
        _hex.GoToOffset(2);
        RecordInspector("message kept until a paint");
        _hex.ReadOnly = true;
        Inspector.BeginEdit(byteRow);
        RecordInspector("edit while read-only");
        _hex.ReadOnly = false;
        Inspector.Focus();
        InspectorModel.SelectedRow = 2;
        Inspector.KeyDown(VirtualKey.Up, false);
        RecordInspector("Up");
        Inspector.KeyDown(VirtualKey.Up, false);
        Inspector.KeyDown(VirtualKey.Up, false);
        RecordInspector("Up at the top");
        Inspector.KeyDown(VirtualKey.Down, false);
        RecordInspector("Down");
        for (int i = 0; i < types.Count + 2; i++)
        {
            Inspector.KeyDown(VirtualKey.Down, false);
        }
        RecordInspector("Down at the end");
        Inspector.KeyDown(VirtualKey.Enter, false);
        RecordInspector("Enter on a row");
        Inspector.EditBoxKey(VirtualKey.Escape);
        Inspector.KeyDown(VirtualKey.F2, false);
        RecordInspector("F2 on a row");
        Inspector.EditBoxKey(VirtualKey.Escape);
        int rowHeight = Inspector.RowHeight;
        int valueX = Inspector.ValueX;
        List<string> rows = new List<string>();
        foreach (int y in new[] { 0, 2 + rowHeight + 3, 2 + rowHeight + 4, 2 + 2 * rowHeight + 3, 2 + 2 * rowHeight + 4, 2 + (2 + types.Count) * rowHeight + 3, 2 + (2 + types.Count) * rowHeight + 4 })
        {
            rows.Add(Inspector.RowAt(y).ToString(CultureInfo.InvariantCulture));
        }
        Record("inspector rows at the edges", string.Join(",", rows));
        Inspector.MouseDown(HexReplayButton.Left, valueX + 2, 2 + rowHeight + 4 + rowHeight / 2);
        RecordInspector("click on the byte order");
        Inspector.MouseDown(HexReplayButton.Left, valueX + 2, 2 + rowHeight + 4 + rowHeight + rowHeight / 2);
        RecordInspector("click on the first value");
        Inspector.MouseDown(HexReplayButton.Right, valueX + 2, 2 + rowHeight + 4 + rowHeight / 2);
        RecordInspector("right click on the byte order");
        Inspector.Focus();
        Inspector.KeyDown(VirtualKey.I, true);
        RecordInspector("Ctrl+I");
        _hex.ShowDataInspector = true;
        Inspector.HideLinkBounds(out int hideX, out int hideY, out _, out _);
        Inspector.MouseDown(HexReplayButton.Left, hideX + 1, hideY + 1);
        RecordInspector("click on Hide");
    }

    // The runs that showed the control in a form counted the focus events of the window in states=: that field is not compared there.
    private static string Comparable(string line, bool shown)
    {
        return shown ? System.Text.RegularExpressions.Regex.Replace(line, " states=[0-9]+ ", " ") : line;
    }

    // A value of the data inspector is a floating point number written with "R", which the golden file has as Mono wrote it (15 digits, or 17
    // when 15 do not read back the same) and .NET 8 writes as the shortest text that reads back the same (since .NET Core 3.0). Two lines that
    // differ only there are the same line: every word that differs reads as the same float or the same double.
    private static bool SameButForNumbers(string expected, string actual)
    {
        char[] separators = { ' ', '[', ']', '=' };
        string[] left = expected.Split(separators);
        string[] right = actual.Split(separators);
        if (left.Length != right.Length)
        {
            return false;
        }
        bool differs = false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i])
            {
                continue;
            }
            if (!double.TryParse(left[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                || !double.TryParse(right[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                return false;
            }
            if (a != b && (float)a != (float)b)
            {
                return false;
            }
            differs = true;
        }
        return differs;
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
            RunMenu();
            RunInspector();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return 1;
        }
        string goldenPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HexEditorControlGolden.txt");
        if (!File.Exists(goldenPath))
        {
            Console.WriteLine("FAIL: " + goldenPath + " is missing");
            return 1;
        }
        string[] golden = File.ReadAllText(goldenPath, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (args.Length == 2 && args[0] == "--write")
        {
            // For a look at what the models wrote (the golden file is only ever written by the control).
            File.WriteAllText(args[1], string.Join("\n", Lines) + "\n", new UTF8Encoding(false));
        }
        int mismatches = 0;
        int numbers = 0;
        bool shown = false;
        for (int i = 0; i < Math.Max(golden.Length, Lines.Count); i++)
        {
            string expected = i < golden.Length ? golden[i] : "(missing)";
            string actual = i < Lines.Count ? Lines[i] : "(missing)";
            if (expected.StartsWith("== ", StringComparison.Ordinal))
            {
                shown = expected == "== view" || expected == "== inspector";
            }
            if (Comparable(expected, shown) != Comparable(actual, shown))
            {
                if (SameButForNumbers(Comparable(expected, shown), Comparable(actual, shown)))
                {
                    numbers++;
                    continue;
                }
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
        Console.WriteLine("PASS: all " + Lines.Count + " lines of the hex editor script on the models match the golden file of the control ("
            + numbers + " of them in a floating point number written as the runtime writes it)");
        Console.WriteLine();
        Console.WriteLine("All hex editor model snapshot checks passed.");
        return 0;
    }
}
