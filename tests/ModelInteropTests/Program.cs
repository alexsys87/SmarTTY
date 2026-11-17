using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SmarTTY.Controls;
using SmarTTY.TextEditing;

// Checks of Controls/ModelInterop.cs against the real WinForms and GDI+ types, which the logic folders no longer use.
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

    private static Dictionary<string, int> Members(Type enumType)
    {
        var result = new Dictionary<string, int>();
        foreach (string name in Enum.GetNames(enumType))
        {
            result[name] = Convert.ToInt32(Enum.Parse(enumType, name));
        }
        return result;
    }

    private static void KeysMatchChecks()
    {
        Dictionary<string, int> keys = Members(typeof(Keys));
        Dictionary<string, int> virtualKeys = Members(typeof(VirtualKey));
        Check(keys.Count > 100, "the Keys enumeration is read (" + keys.Count + " members)");

        string[] onlyKeys = keys.Keys.Where(k => !virtualKeys.ContainsKey(k)).OrderBy(k => k).ToArray();
        string[] onlyVirtual = virtualKeys.Keys.Where(k => !keys.ContainsKey(k)).OrderBy(k => k).ToArray();
        Equal(string.Join(", ", onlyKeys), "", "every member of Keys has a member of VirtualKey with the same name");
        Equal(string.Join(", ", onlyVirtual), "", "every member of VirtualKey has a member of Keys with the same name");

        string[] different = keys.Where(k => virtualKeys.ContainsKey(k.Key) && virtualKeys[k.Key] != k.Value).Select(k => k.Key + " " + k.Value + " vs " + virtualKeys[k.Key]).ToArray();
        Equal(string.Join(", ", different), "", "every member has the same value in both enumerations");

        Equal(Enum.GetUnderlyingType(typeof(VirtualKey)), Enum.GetUnderlyingType(typeof(Keys)), "both enumerations have the same underlying type");
        Equal(typeof(VirtualKey).IsDefined(typeof(FlagsAttribute), false), typeof(Keys).IsDefined(typeof(FlagsAttribute), false), "both enumerations are [Flags] alike");
        Equal((int)VirtualKey.KeyCode, (int)Keys.KeyCode, "KeyCode");
        Equal((int)VirtualKey.Modifiers, (int)Keys.Modifiers, "Modifiers");
        Equal((int)VirtualKey.Shift, (int)Keys.Shift, "Shift");
        Equal((int)VirtualKey.Control, (int)Keys.Control, "Control");
        Equal((int)VirtualKey.Alt, (int)Keys.Alt, "Alt");
    }

    private static void KeyConversionChecks()
    {
        int checkedValues = 0;
        foreach (Keys key in Enum.GetValues(typeof(Keys)))
        {
            Keys back = key.ToVirtualKey().ToKeys();
            if (back != key || (int)key.ToVirtualKey() != (int)key)
            {
                throw new Exception("FAIL: " + key + " does not survive the round trip through VirtualKey");
            }
            checkedValues++;
        }
        Check(checkedValues > 100, "every member of Keys survives the round trip through VirtualKey (" + checkedValues + " members)");

        // Combinations with modifiers, as in KeyData of a key event, for every key code and every modifier set.
        int combinations = 0;
        foreach (Keys modifiers in new[] { Keys.None, Keys.Shift, Keys.Control, Keys.Alt, Keys.Shift | Keys.Control, Keys.Control | Keys.Alt, Keys.Shift | Keys.Alt, Keys.Shift | Keys.Control | Keys.Alt })
        {
            for (int code = 0; code <= 255; code++)
            {
                Keys key = (Keys)code | modifiers;
                if (key.ToVirtualKey().ToKeys() != key || (int)key.ToVirtualKey() != (int)key)
                {
                    throw new Exception("FAIL: " + key + " does not survive the round trip");
                }
                combinations++;
            }
        }
        Check(combinations == 8 * 256, "all " + combinations + " key codes with all modifier sets survive the round trip");

        Equal(Keys.Control.ToVirtualKey() | Keys.C.ToVirtualKey(), (Keys.Control | Keys.C).ToVirtualKey(), "modifiers combine the same way");
        Equal(((Keys.Control | Keys.Shift | Keys.F5).ToVirtualKey() & VirtualKey.KeyCode), VirtualKey.F5, "KeyCode strips the modifiers of a converted value");

        var e = new KeyEventArgs(Keys.Control | Keys.Shift | Keys.Left);
        KeyStroke stroke = e.ToKeyStroke();
        Equal(stroke.KeyData, VirtualKey.Control | VirtualKey.Shift | VirtualKey.Left, "KeyEventArgs gives the key data");
        Equal(stroke.KeyCode, VirtualKey.Left, "KeyEventArgs gives the key code");
        Check(stroke.Control && stroke.Shift && !stroke.Alt, "KeyEventArgs gives the modifiers");
        Equal(stroke.Char, '\0', "a key-down event has no character");
        var plain = new KeyEventArgs(Keys.A).ToKeyStroke();
        Equal(plain, new KeyStroke(VirtualKey.A), "KeyEventArgs for a plain key");
        var alt = new KeyEventArgs(Keys.Alt | Keys.F4).ToKeyStroke();
        Check(alt.Alt && alt.KeyCode == VirtualKey.F4, "KeyEventArgs for Alt+F4");
    }

    private static void ColorChecks()
    {
        Equal(Color.FromArgb(10, 20, 30, 40).ToRgb(), RgbColor.FromArgb(10, 20, 30, 40), "a colour converts to the components");
        Equal(RgbColor.FromArgb(10, 20, 30, 40).ToColor(), Color.FromArgb(10, 20, 30, 40), "components convert to a colour");
        Equal(RgbColor.FromArgb(10, 20, 30, 40).ToColor().ToArgb(), RgbColor.FromArgb(10, 20, 30, 40).ToArgb(), "ToArgb of the converted colour is the same number");

        Check(Color.Empty.ToRgb().IsEmpty, "Color.Empty becomes RgbColor.Empty");
        Equal(RgbColor.Empty.ToColor(), Color.Empty, "RgbColor.Empty becomes Color.Empty");
        Check(RgbColor.Empty.ToColor().IsEmpty, "the colour made from RgbColor.Empty is empty");

        // The trap of the GDI+ colour: it compares "named" too, so a named colour differs from the same colour made of components.
        Check(Color.Red != Color.FromArgb(255, 0, 0), "(the GDI+ colour) Color.Red != Color.FromArgb(255, 0, 0)");
        Equal(Color.Red.ToRgb(), Color.FromArgb(255, 0, 0).ToRgb(), "after the conversion the named and the built colour are equal");
        Check(Color.Red.ToRgb() == RgbColor.FromArgb(255, 255, 0, 0), "Color.Red equals opaque red components");
        Equal(SystemColors.Window.ToRgb().ToArgb(), SystemColors.Window.ToArgb(), "a system colour is resolved to its value");

        int sampled = 0;
        for (int i = 0; i < 4096; i++)
        {
            int argb = unchecked((int)(0x9E3779B1u * (uint)(i + 1)));
            Color c = Color.FromArgb(argb);
            if (c.ToRgb().ToColor().ToArgb() != argb && argb != 0)
            {
                throw new Exception("FAIL: colour " + argb.ToString("X8") + " does not survive the round trip");
            }
            sampled++;
        }
        Check(sampled == 4096, "4096 sample colours survive the round trip through RgbColor");

        // A documented difference: fully transparent black is not distinguished from "not set".
        Check(Color.FromArgb(0, 0, 0, 0).ToRgb().IsEmpty, "fully transparent black becomes the empty colour");
        Check(Color.Transparent.ToRgb() != RgbColor.Empty, "Color.Transparent (white, alpha 0) is not the empty colour");
        Equal(Color.Transparent.ToRgb().ToColor().ToArgb(), Color.Transparent.ToArgb(), "Color.Transparent keeps its components");
    }

    private static void PointChecks()
    {
        Equal(new GridPoint(5, 9).ToPoint(), new Point(5, 9), "GridPoint to Point: X is the column, Y the row");
        Equal(new Point(5, 9).ToGridPoint(), new GridPoint(5, 9), "Point to GridPoint");
        Equal(new GridPoint(-3, -1).ToPoint().ToGridPoint(), new GridPoint(-3, -1), "negative values survive the round trip");
        Equal(GridPoint.Empty.ToPoint(), Point.Empty, "GridPoint.Empty is Point.Empty");
        Equal(default(GridPoint).ToPoint().IsEmpty, true, "default(GridPoint) gives the empty point");
    }

    private static void HighlightBrushChecks()
    {
        // The model holds colours; the view makes the brushes and pens, as the former form of the style did.
        var marker = new HighlightStyle(Color.FromArgb(45, 90, 140, 255).ToRgb(), Color.FromArgb(150, 70, 120, 230).ToRgb(), RgbColor.Empty);
        Brush fill = HighlightBrushes.FillBrush(marker);
        Check(fill is SolidBrush, "the fill is a solid brush");
        Equal(((SolidBrush)fill).Color.ToArgb(), Color.FromArgb(45, 90, 140, 255).ToArgb(), "the fill brush has the colour of the style");
        Pen border = HighlightBrushes.BorderPen(marker);
        Equal(border.Color.ToArgb(), Color.FromArgb(150, 70, 120, 230).ToArgb(), "the border pen has the colour of the style");
        Equal(border.Width, 1f, "the border pen is 1 pixel wide");
        Check(HighlightBrushes.TextBrush(marker) == null, "a style without a text colour has no text brush");
        Check(ReferenceEquals(fill, HighlightBrushes.FillBrush(marker)), "the brush of a colour is made once");
        Check(ReferenceEquals(border, HighlightBrushes.BorderPen(marker)), "the pen of a colour is made once");
        var same = new HighlightStyle(Color.FromArgb(45, 90, 140, 255).ToRgb(), RgbColor.Empty, RgbColor.Empty);
        Check(ReferenceEquals(fill, HighlightBrushes.FillBrush(same)), "two styles of the same colour share the brush");
        Check(HighlightBrushes.BorderPen(same) == null, "a style without a border has no pen");

        var none = new HighlightStyle(RgbColor.Empty, RgbColor.Empty, RgbColor.Empty);
        Check(HighlightBrushes.FillBrush(none) == null && HighlightBrushes.BorderPen(none) == null && HighlightBrushes.TextBrush(none) == null, "a style of empty colours has no brush and no pen");

        var selection = new HighlightStyle();
        Equal(((SolidBrush)HighlightBrushes.FillBrush(selection)).Color.ToArgb(), SystemColors.Highlight.ToArgb(), "the default style fills with the system selection colour");
        Equal(((SolidBrush)HighlightBrushes.TextBrush(selection)).Color.ToArgb(), SystemColors.HighlightText.ToArgb(), "the default style has the system selected text colour");
        Check(HighlightBrushes.BorderPen(selection) == null, "the default style has no border");

        // A colour of the style wins over the system colour.
        selection.FillColor = RgbColor.FromArgb(1, 2, 3);
        Equal(((SolidBrush)HighlightBrushes.FillBrush(selection)).Color.ToArgb(), Color.FromArgb(1, 2, 3).ToArgb(), "a colour of the style wins over the system colour");
    }

    private static void Main()
    {
        HighlightBrushChecks();
        KeysMatchChecks();
        KeyConversionChecks();
        ColorChecks();
        PointChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " model interop checks passed.");
    }
}
