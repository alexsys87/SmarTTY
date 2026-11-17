using System;
using System.Collections.Generic;
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

    private static void Main()
    {
        RgbColorChecks();
        GridPointChecks();
        KeyStrokeChecks();
        VirtualKeyChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " model type checks passed.");
    }
}
