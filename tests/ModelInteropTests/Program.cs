using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;

// Checks of VirtualKey against the real WinForms keys and of the colour values of the settings against the named GDI+ colours they
// replaced, which the logic folders and (since stages 5B and 5C) the application no longer use.
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

    // Stage 5C replaced the named colours of System.Drawing in the defaults of the settings and of the terminals by their numbers, each
    // followed by the name as a comment: "RgbColor.FromArgb(211, 211, 211) /* LightGray */". Every such number is the named colour.
    private static void NamedColorChecks()
    {
        string root = RepositoryRoot();
        Regex named = new Regex(@"RgbColor\.FromArgb\((\d+), (\d+), (\d+)\) /\* (\w+) \*/");
        int found = 0;
        foreach (string file in new[] { "VisualTTY/GlobalSettings.cs", "VisualTTY/DeviceTerminal/DeviceTerminalSettings.cs", "GUI/DevicePanel/WpfDeviceTerminal.cs" })
        {
            foreach (Match match in named.Matches(File.ReadAllText(Path.Combine(root, file))))
            {
                Color color = Color.FromName(match.Groups[4].Value);
                Check(color.IsKnownColor, file + ": " + match.Groups[4].Value + " is a named colour");
                Equal(RgbColor.FromArgb(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)).ToArgb(), color.ToArgb(), file + ": " + match.Value);
                found++;
            }
        }
        Check(found >= 12, "the named colours of the defaults are found (" + found + ")");
        // The light grey that stands for SystemColors.ButtonFace where the system cannot be asked (Native/SystemColorValues.cs).
        Check(File.ReadAllText(Path.Combine(root, "Native/SystemColorValues.cs")).Contains("RgbColor.FromArgb(240, 240, 240)"), "the fallback of ButtonFace is the light grey of Windows");
        Equal(RgbColor.FromArgb(10, 20, 30, 40).ToArgb(), Color.FromArgb(10, 20, 30, 40).ToArgb(), "RgbColor packs the components as the GDI+ colour did");
        Equal(RgbColor.FromArgb(1, 2, 3).ToArgb(), Color.FromArgb(1, 2, 3).ToArgb(), "a colour of three components is opaque in both");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SmarTTY.csproj")))
        {
            directory = directory.Parent;
        }
        if (directory == null)
        {
            throw new Exception("FAIL: the repository (SmarTTY.csproj) is not above " + AppDomain.CurrentDomain.BaseDirectory);
        }
        return directory.FullName;
    }

    private static void Main()
    {
        KeysMatchChecks();
        KeyConversionChecks();
        NamedColorChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " model interop checks passed.");
    }
}

// The conversions between the WinForms keys and VirtualKey (they were in Controls/ModelInterop.cs until stage 5B; the application no
// longer has the WinForms keys, the test keeps them to check that VirtualKey is still an exact copy).
internal static class KeyInterop
{
    public static VirtualKey ToVirtualKey(this Keys keys)
    {
        return (VirtualKey)(int)keys;
    }

    public static Keys ToKeys(this VirtualKey key)
    {
        return (Keys)(int)key;
    }

    public static KeyStroke ToKeyStroke(this KeyEventArgs e)
    {
        return new KeyStroke(e.KeyData.ToVirtualKey());
    }
}
