using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of the types that replace the GDI+ and WinForms types in the logic folders: RgbColor, GridPoint, VirtualKey and KeyStroke
// (TextEditing/). Their match with the real colour, point and keys types is checked by ModelInteropTests, which needs WinForms.
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

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return true;
        }
        return false;
    }

    private static void RgbColorChecks()
    {
        RgbColor c = RgbColor.FromArgb(10, 20, 30, 40);
        Equal(c.A, (byte)10, "RgbColor alpha");
        Equal(c.R, (byte)20, "RgbColor red");
        Equal(c.G, (byte)30, "RgbColor green");
        Equal(c.B, (byte)40, "RgbColor blue");
        Equal(c.Argb, 0x0A141E28u, "RgbColor packs 0xAARRGGBB");
        Equal(RgbColor.FromArgb(1, 2, 3).A, (byte)255, "RgbColor built from three components is opaque");
        Equal(RgbColor.FromArgb(255, 0, 0), RgbColor.FromArgb(255, 255, 0, 0), "RgbColor red equals the same colour with alpha 255");

        Check(RgbColor.Empty.IsEmpty, "RgbColor.Empty is empty");
        Check(default(RgbColor).IsEmpty, "default(RgbColor) is empty");
        Equal(default(RgbColor), RgbColor.Empty, "default(RgbColor) equals RgbColor.Empty");
        Check(!RgbColor.FromArgb(0, 0, 0).IsEmpty, "opaque black is not empty");
        Check(!RgbColor.FromArgb(1, 0, 0, 0).IsEmpty, "almost transparent black is not empty");
        Check(RgbColor.FromArgb(0, 0, 0, 0).IsEmpty, "fully transparent black is the empty colour (documented difference from the GDI+ type)");
        var colors = new RgbColor[3];
        Check(colors[0].IsEmpty && colors[2].IsEmpty, "a new array of RgbColor holds empty colours");

        // The signed form is what ToArgb() of the GDI+ colour returns: 0xFFFF0000 for opaque red is a negative number.
        int red = unchecked((int)0xFFFF0000);
        Equal(RgbColor.FromArgb(red), RgbColor.FromArgb(255, 255, 0, 0), "RgbColor from the signed ToArgb() value");
        Equal(RgbColor.FromArgb(255, 255, 0, 0).ToArgb(), red, "RgbColor.ToArgb returns the signed value");
        Equal(RgbColor.FromArgb(0).IsEmpty, true, "RgbColor from signed 0 is empty");
        Equal(RgbColor.FromArgb(int.MinValue).Argb, 0x80000000u, "RgbColor keeps the sign bit");

        Check(RgbColor.FromArgb(1, 2, 3) == RgbColor.FromArgb(1, 2, 3), "== on equal colours");
        Check(RgbColor.FromArgb(1, 2, 3) != RgbColor.FromArgb(1, 2, 4), "!= on different colours");
        Check(RgbColor.FromArgb(128, 1, 2, 3) != RgbColor.FromArgb(1, 2, 3), "alpha takes part in the comparison");
        Check(RgbColor.FromArgb(1, 2, 3).Equals((object)RgbColor.FromArgb(1, 2, 3)), "Equals(object) on equal colours");
        Check(!RgbColor.FromArgb(1, 2, 3).Equals("x"), "Equals(object) on another type");
        Equal(RgbColor.FromArgb(1, 2, 3).GetHashCode(), RgbColor.FromArgb(1, 2, 3).GetHashCode(), "equal colours have equal hash codes");
        var set = new HashSet<RgbColor> { RgbColor.FromArgb(1, 2, 3), RgbColor.FromArgb(1, 2, 3), RgbColor.Empty };
        Equal(set.Count, 2, "RgbColor works as a hash set key");

        Check(Throws<ArgumentOutOfRangeException>(() => RgbColor.FromArgb(256, 0, 0)), "a component above 255 is rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => RgbColor.FromArgb(0, -1, 0)), "a negative component is rejected");
        Check(Throws<ArgumentOutOfRangeException>(() => RgbColor.FromArgb(300, 0, 0, 0)), "an alpha above 255 is rejected");

        Equal(RgbColor.FromArgb(255, 0, 0).ToString(), "RgbColor [FFFF0000]", "RgbColor.ToString");
        Equal(RgbColor.Empty.ToString(), "RgbColor [Empty]", "RgbColor.Empty.ToString");
    }

    private static void GridPointChecks()
    {
        var p = new GridPoint(3, 7);
        Equal(p.X, 3, "GridPoint column");
        Equal(p.Y, 7, "GridPoint row");
        Check(GridPoint.Empty.IsEmpty && default(GridPoint).IsEmpty, "GridPoint.Empty and default are (0, 0)");
        Check(!p.IsEmpty, "(3, 7) is not empty");

        // The reason for a mutable struct: code such as buffer.Cursor.X++ must keep working.
        var holder = new Holder { Cursor = new GridPoint(1, 2) };
        holder.Cursor.X++;
        holder.Cursor.Y += 5;
        Equal(holder.Cursor, new GridPoint(2, 7), "a field of a GridPoint inside a class can be changed in place");
        var copy = holder.Cursor;
        copy.X = 99;
        Equal(holder.Cursor.X, 2, "GridPoint is a value: a copy does not change the original");

        Check(new GridPoint(1, 2) == new GridPoint(1, 2), "== on equal points");
        Check(new GridPoint(1, 2) != new GridPoint(2, 1), "!= on swapped column and row");
        Check(new GridPoint(1, 2).Equals((object)new GridPoint(1, 2)), "Equals(object) on equal points");
        Check(!new GridPoint(1, 2).Equals(1), "Equals(object) on another type");
        Equal(new GridPoint(1, 2).GetHashCode(), new GridPoint(1, 2).GetHashCode(), "equal points have equal hash codes");
        Equal(new GridPoint(4, 5).ToString(), "{X=4,Y=5}", "GridPoint.ToString");
        Equal(new GridPoint(-1, -1), new GridPoint(-1, -1), "negative coordinates (a position outside the grid) compare equal");
    }

    private sealed class Holder
    {
        public GridPoint Cursor;
    }

    private static void KeyStrokeChecks()
    {
        var plain = new KeyStroke(VirtualKey.A);
        Equal(plain.KeyData, VirtualKey.A, "KeyStroke key data");
        Equal(plain.Char, '\0', "KeyStroke without a character");
        Equal(plain.KeyCode, VirtualKey.A, "KeyStroke key code");
        Check(!plain.Shift && !plain.Control && !plain.Alt, "a plain key has no modifiers");

        var combo = new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5);
        Equal(combo.KeyCode, VirtualKey.F5, "KeyCode strips the modifiers");
        Check(combo.Control && combo.Shift && !combo.Alt, "Control and Shift are read from the key data");
        Check(new KeyStroke(VirtualKey.Alt | VirtualKey.Left).Alt, "Alt is read from the key data");

        var typed = new KeyStroke(VirtualKey.D1 | VirtualKey.Shift, '!');
        Equal(typed.Char, '!', "KeyStroke keeps the typed character");
        Check(typed != new KeyStroke(VirtualKey.D1 | VirtualKey.Shift), "the character takes part in the comparison");
        Check(typed == new KeyStroke(VirtualKey.D1 | VirtualKey.Shift, '!'), "equal key strokes");
        Equal(typed.GetHashCode(), new KeyStroke(VirtualKey.D1 | VirtualKey.Shift, '!').GetHashCode(), "equal key strokes have equal hash codes");
        Check(typed.Equals((object)new KeyStroke(VirtualKey.D1 | VirtualKey.Shift, '!')), "Equals(object) on equal key strokes");
        Equal(new KeyStroke(VirtualKey.Control | VirtualKey.C).ToString(), "C, Control", "KeyStroke.ToString without a character");
    }

    private static void VirtualKeyChecks()
    {
        // The values below are the documented values of the WinForms Keys enumeration; the full match is checked by ModelInteropTests.
        Equal((int)VirtualKey.KeyCode, 0xFFFF, "VirtualKey.KeyCode");
        Equal((int)VirtualKey.Shift, 0x10000, "VirtualKey.Shift");
        Equal((int)VirtualKey.Control, 0x20000, "VirtualKey.Control");
        Equal((int)VirtualKey.Alt, 0x40000, "VirtualKey.Alt");
        Equal(unchecked((uint)VirtualKey.Modifiers), 0xFFFF0000u, "VirtualKey.Modifiers");
        Equal((int)VirtualKey.Return, 13, "VirtualKey.Return");
        Equal(VirtualKey.Enter, VirtualKey.Return, "VirtualKey.Enter is Return");
        Equal(VirtualKey.PageDown, VirtualKey.Next, "VirtualKey.PageDown is Next");
        Equal(VirtualKey.PageUp, VirtualKey.Prior, "VirtualKey.PageUp is Prior");
        Equal((int)VirtualKey.A, 65, "VirtualKey.A");
        Equal((int)VirtualKey.F12, 123, "VirtualKey.F12");
        Equal((int)VirtualKey.OemClear, 254, "VirtualKey.OemClear");
        Equal(VirtualKey.None, (VirtualKey)0, "VirtualKey.None");
        Equal((VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5) & VirtualKey.KeyCode, VirtualKey.F5, "modifiers are removed with KeyCode");
        Equal((VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5) & VirtualKey.Modifiers, VirtualKey.Control | VirtualKey.Shift, "modifiers are kept with Modifiers");
        Equal(Enum.GetValues(typeof(VirtualKey)).Length, 194, "VirtualKey has as many members as the WinForms Keys enumeration");
    }

    private static void HighlightStyleChecks()
    {
        // The style of a selection: the system colours (the view resolves them when it paints), no border.
        var selection = new HighlightStyle();
        Check(selection.UseSystemSelectionColors, "the default style uses the system selection colours");
        Check(selection.FillColor.IsEmpty && selection.TextColor.IsEmpty && selection.BorderColor.IsEmpty, "the default style holds no colours of its own");

        var marker = new HighlightStyle(RgbColor.FromArgb(45, 90, 140, 255), RgbColor.FromArgb(150, 70, 120, 230), RgbColor.Empty);
        Check(!marker.UseSystemSelectionColors, "a style with colours does not use the system colours");
        Equal(marker.FillColor, RgbColor.FromArgb(45, 90, 140, 255), "style fill colour");
        Equal(marker.BorderColor, RgbColor.FromArgb(150, 70, 120, 230), "style border colour");
        Check(marker.TextColor.IsEmpty, "a style without a text colour keeps the text colour of the text");

        var none = new HighlightStyle(RgbColor.Empty, RgbColor.Empty, RgbColor.Empty);
        Check(!none.UseSystemSelectionColors && none.FillColor.IsEmpty && none.BorderColor.IsEmpty && none.TextColor.IsEmpty, "a style of empty colours draws nothing");

        marker.FillColor = RgbColor.FromArgb(1, 2, 3);
        Equal(marker.FillColor, RgbColor.FromArgb(1, 2, 3), "the colours of a style can be changed");
    }

    // Records what a command does with the editor; the predicate of a token jump is asked about the brackets.
    private sealed class RecordingTarget : IEditorCommandTarget
    {
        public readonly List<string> Calls = new List<string>();

        public void ShowCompletion() { Calls.Add("ShowCompletion()"); }

        public void Retokenize() { Calls.Add("Retokenize()"); }

        public bool MoveToToken(bool searchPrevious, bool isOpening, bool selectText, TokenPredicate callback)
        {
            string accepted = "";
            foreach (char bracket in "()[]{}<>")
            {
                if (callback(bracket.ToString()))
                {
                    accepted += bracket;
                }
            }
            Calls.Add("MoveToToken(" + searchPrevious + "," + isOpening + "," + selectText + ",\"" + accepted + "\")");
            return true;
        }

        public void ExpandCurrentFold() { Calls.Add("ExpandCurrentFold()"); }

        public void CollapseCurrentFold() { Calls.Add("CollapseCurrentFold()"); }

        public void DuplicateLines() { Calls.Add("DuplicateLines()"); }

        public void MoveLines(int direction) { Calls.Add("MoveLines(" + direction + ")"); }

        public void DeleteLines() { Calls.Add("DeleteLines()"); }

        public bool ToggleComment() { Calls.Add("ToggleComment()"); return true; }

        public void ZoomBy(int steps) { Calls.Add("ZoomBy(" + steps + ")"); }

        public void ResetZoom() { Calls.Add("ResetZoom()"); }

        public void AddNextOccurrence() { Calls.Add("AddNextOccurrence()"); }

        public void SelectAllOccurrences() { Calls.Add("SelectAllOccurrences()"); }

        public void AddCursorAbove() { Calls.Add("AddCursorAbove()"); }

        public void AddCursorBelow() { Calls.Add("AddCursorBelow()"); }
    }

    private static void EditorCommandChecks()
    {
        // Name, modifiers (C = Control, A = Alt, S = Shift), key, calls of the command on the editor.
        // Taken from the editor commands before they were moved from CodeEditorPP to IEditorCommandTarget and from Keys to VirtualKey.
        object[][] expected =
        {
            new object[] { "ShowCompletion", "C", VirtualKey.Space, "ShowCompletion()" },
            new object[] { "RetokenizeDocument", "C", VirtualKey.R, "Retokenize()" },
            new object[] { "GoToOpeningRoundBracket", "CA", VirtualKey.D9, "MoveToToken(True,False,False,\"(\")" },
            new object[] { "SelectToOpeningRoundBracket", "CS", VirtualKey.D9, "MoveToToken(True,False,True,\"(\")" },
            new object[] { "GoToClosingRoundBracket", "CA", VirtualKey.D0, "MoveToToken(False,False,False,\")\")" },
            new object[] { "SelectToClosingRoundBracket", "CS", VirtualKey.D0, "MoveToToken(False,False,True,\")\")" },
            new object[] { "GoToOpeningCurlyBracket", "CA", VirtualKey.OemOpenBrackets, "MoveToToken(True,False,False,\"{\")" },
            new object[] { "SelectToOpeningCurlyBracket", "CAS", VirtualKey.OemOpenBrackets, "MoveToToken(True,False,True,\"{\")" },
            new object[] { "GoToClosingCurlyBracket", "CA", VirtualKey.OemCloseBrackets, "MoveToToken(False,False,False,\"}\")" },
            new object[] { "SelectToClosingCurlyBracket", "CAS", VirtualKey.OemCloseBrackets, "MoveToToken(False,False,True,\"}\")" },
            new object[] { "GoToOpeningSquareBracket", "C", VirtualKey.OemOpenBrackets, "MoveToToken(True,False,False,\"[\")" },
            new object[] { "SelectToOpeningSquareBracket", "CS", VirtualKey.OemOpenBrackets, "MoveToToken(True,False,True,\"[\")" },
            new object[] { "GoToClosingSquareBracket", "C", VirtualKey.OemCloseBrackets, "MoveToToken(False,False,False,\"]\")" },
            new object[] { "SelectToClosingSquareBracket", "CS", VirtualKey.OemCloseBrackets, "MoveToToken(False,False,True,\"]\")" },
            new object[] { "GoToOpeningAngularBracket", "C", VirtualKey.Oemcomma, "MoveToToken(True,False,False,\"<\")" },
            new object[] { "SelectToOpeningAngularBracket", "CS", VirtualKey.Oemcomma, "MoveToToken(True,False,True,\"<\")" },
            new object[] { "GoToClosingAngularBracket", "C", VirtualKey.OemPeriod, "MoveToToken(False,False,False,\">\")" },
            new object[] { "SelectToClosingAngularBracket", "CS", VirtualKey.OemPeriod, "MoveToToken(False,False,True,\">\")" },
            new object[] { "ExpandHiddenBlockNearCursor", "CA", VirtualKey.Right, "ExpandCurrentFold()" },
            new object[] { "CollapseHiddenBlockNearCursor", "CA", VirtualKey.Left, "CollapseCurrentFold()" },
            new object[] { "AddNextOccurrence", "C", VirtualKey.D, "AddNextOccurrence()" },
            new object[] { "SelectAllOccurrences", "CS", VirtualKey.L, "SelectAllOccurrences()" },
            new object[] { "AddCursorAbove", "CA", VirtualKey.Up, "AddCursorAbove()" },
            new object[] { "AddCursorBelow", "CA", VirtualKey.Down, "AddCursorBelow()" },
            new object[] { "DuplicateLines", "CS", VirtualKey.D, "DuplicateLines()" },
            new object[] { "MoveLinesUp", "A", VirtualKey.Up, "MoveLines(-1)" },
            new object[] { "MoveLinesDown", "A", VirtualKey.Down, "MoveLines(1)" },
            new object[] { "DeleteLines", "CS", VirtualKey.K, "DeleteLines()" },
            new object[] { "ToggleComment", "C", VirtualKey.OemQuestion, "ToggleComment()" },
            new object[] { "ToggleCommentNumpad", "C", VirtualKey.Divide, "ToggleComment()" },
            new object[] { "ToggleCommentShiftedDigit", "CS", VirtualKey.D7, "ToggleComment()" },
            new object[] { "ZoomIn", "C", VirtualKey.Oemplus, "ZoomBy(1)" },
            new object[] { "ZoomInShifted", "CS", VirtualKey.Oemplus, "ZoomBy(1)" },
            new object[] { "ZoomInNumpad", "C", VirtualKey.Add, "ZoomBy(1)" },
            new object[] { "ZoomOut", "C", VirtualKey.OemMinus, "ZoomBy(-1)" },
            new object[] { "ZoomOutNumpad", "C", VirtualKey.Subtract, "ZoomBy(-1)" },
            new object[] { "ZoomReset", "C", VirtualKey.D0, "ResetZoom()" },
            new object[] { "ZoomResetNumpad", "C", VirtualKey.NumPad0, "ResetZoom()" },
        };
        Equal(CommandCount(), expected.Length, "the editor has " + expected.Length + " commands");
        foreach (object[] row in expected)
        {
            string name = (string)row[0];
            string modifiers = (string)row[1];
            VirtualKey key = (VirtualKey)row[2];
            KeyBindingMap.EditorCommand command = new KeyBindingMap().FindCommand(name);
            KeyBindingMap.KeyShortcut shortcut = command.DefaultShortcut;
            string actualModifiers = (shortcut.Ctrl ? "C" : "") + (shortcut.Alt ? "A" : "") + (shortcut.Shift ? "S" : "");
            if (actualModifiers != modifiers || shortcut.Key != key)
            {
                throw new Exception("FAIL: shortcut of " + name + " is " + actualModifiers + "+" + shortcut.Key + ", expected " + modifiers + "+" + key);
            }
            RecordingTarget target = new RecordingTarget();
            command.Execute(target);
            if (string.Join(";", target.Calls) != (string)row[3])
            {
                throw new Exception("FAIL: " + name + " did " + string.Join(";", target.Calls) + ", expected " + (string)row[3]);
            }
        }
        Check(true, "every editor command keeps its name, its shortcut and what it does with the editor (" + expected.Length + " commands)");

        // The shortcuts are bound in the order of the commands, a shortcut maps to one command.
        var map = new KeyBindingMap();
        Equal(map.Bindings.Count, expected.Length, "every command has its own shortcut");
        KeyBindingMap.EditorCommand found;
        found = map.Bindings[new KeyBindingMap.KeyShortcut(new KeyStroke(VirtualKey.Control | VirtualKey.Space))];
        Equal(found.Name, "ShowCompletion", "Ctrl+Space is bound to ShowCompletion");
        found = map.Bindings[new KeyBindingMap.KeyShortcut(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.K))];
        Equal(found.Name, "DeleteLines", "Ctrl+Shift+K is bound to DeleteLines");
        Check(!map.Bindings.ContainsKey(new KeyBindingMap.KeyShortcut(new KeyStroke(VirtualKey.Space))), "Space alone is not a shortcut");
        Check(Throws<InvalidOperationException>(() => map.FindCommand("NoSuchCommand")), "an unknown command is an error");

        // A command called with something that is not an editor does what it did before: fails.
        Check(Throws<NullReferenceException>(() => new KeyBindingMap().FindCommand("ShowCompletion").Execute("not an editor")), "a command needs an editor to work on");
    }

    private static int CommandCount()
    {
        return EditorCommands.All.Count;
    }

    private static void KeyShortcutChecks()
    {
        var shortcut = new KeyBindingMap.KeyShortcut { Ctrl = true, Shift = true, Key = VirtualKey.F5 };
        Check(shortcut.Matches(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5)), "a shortcut matches its key press");
        Check(!shortcut.Matches(new KeyStroke(VirtualKey.Control | VirtualKey.F5)), "a missing Shift does not match");
        Check(!shortcut.Matches(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.Alt | VirtualKey.F5)), "an extra Alt does not match");
        Check(!shortcut.Matches(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.F6)), "another key does not match");
        Check(shortcut.Matches(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5, 'x')), "the typed character does not matter");

        var fromKey = new KeyBindingMap.KeyShortcut(new KeyStroke(VirtualKey.Control | VirtualKey.Shift | VirtualKey.F5));
        Check(fromKey.Equals(shortcut), "a shortcut made of a key press equals the same shortcut");
        Equal(fromKey.GetHashCode(), shortcut.GetHashCode(), "equal shortcuts have equal hash codes");
        Check(!fromKey.Equals(new KeyBindingMap.KeyShortcut(new KeyStroke(VirtualKey.F5))), "shortcuts with other modifiers differ");
        Check(!fromKey.Equals("Ctrl+Shift+F5"), "a shortcut is not equal to text");
        Check(fromKey.Ctrl && fromKey.Shift && !fromKey.Alt && fromKey.Key == VirtualKey.F5, "a shortcut takes the modifiers and the key code of a key press");
    }

    private static void Main()
    {
        EditorCommandChecks();
        KeyShortcutChecks();
        HighlightStyleChecks();
        RgbColorChecks();
        GridPointChecks();
        KeyStrokeChecks();
        VirtualKeyChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " model type checks passed.");
    }
}
