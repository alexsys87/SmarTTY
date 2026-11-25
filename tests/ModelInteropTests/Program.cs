using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SmarTTY.TextEditing;

// Checks of VirtualKey against the WinForms keys and of the colour values of the settings against the named GDI+ colours they replaced, which
// the logic folders and (since stages 5B and 5C) the application no longer use. The keys are those of KeysGolden.txt: every member of
// System.Windows.Forms.Keys with its value, the underlying type and the [Flags] attribute, as the WinForms of .NET Framework had them,
// written by this stand while it still ran on Mono (with the WinForms of the system), so that it runs on .NET on any machine now. The named
// colours are those of System.Drawing.Color, which .NET has on every platform.
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

    // The members of the WinForms keys as KeysGolden.txt has them, with its underlying type and whether it is [Flags].
    private sealed class RecordedKeys
    {
        public readonly Dictionary<string, int> Members = new Dictionary<string, int>();

        public string UnderlyingType;

        public bool Flags;

        public int this[string name] => Members[name];
    }

    private static RecordedKeys ReadKeys()
    {
        RecordedKeys keys = new RecordedKeys();
        foreach (string line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "ModelInteropTests", "KeysGolden.txt")))
        {
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            string[] parts = line.Split(' ');
            if (parts.Length != 2)
            {
                throw new Exception("FAIL: a line of KeysGolden.txt is not a name and a value: " + line);
            }
            if (parts[0] == "underlying")
            {
                keys.UnderlyingType = parts[1];
            }
            else if (parts[0] == "flags")
            {
                keys.Flags = bool.Parse(parts[1]);
            }
            else
            {
                keys.Members[parts[0]] = int.Parse(parts[1]);
            }
        }
        return keys;
    }

    private static void KeysMatchChecks()
    {
        RecordedKeys recorded = ReadKeys();
        Dictionary<string, int> keys = recorded.Members;
        Dictionary<string, int> virtualKeys = Members(typeof(VirtualKey));
        Check(keys.Count > 100, "the Keys enumeration is read (" + keys.Count + " members)");

        string[] onlyKeys = keys.Keys.Where(k => !virtualKeys.ContainsKey(k)).OrderBy(k => k).ToArray();
        string[] onlyVirtual = virtualKeys.Keys.Where(k => !keys.ContainsKey(k)).OrderBy(k => k).ToArray();
        Equal(string.Join(", ", onlyKeys), "", "every member of Keys has a member of VirtualKey with the same name");
        Equal(string.Join(", ", onlyVirtual), "", "every member of VirtualKey has a member of Keys with the same name");

        string[] different = keys.Where(k => virtualKeys.ContainsKey(k.Key) && virtualKeys[k.Key] != k.Value).Select(k => k.Key + " " + k.Value + " vs " + virtualKeys[k.Key]).ToArray();
        Equal(string.Join(", ", different), "", "every member has the same value in both enumerations");

        Equal(Enum.GetUnderlyingType(typeof(VirtualKey)).Name, recorded.UnderlyingType, "both enumerations have the same underlying type");
        Equal(typeof(VirtualKey).IsDefined(typeof(FlagsAttribute), false), recorded.Flags, "both enumerations are [Flags] alike");
        Equal((int)VirtualKey.KeyCode, recorded["KeyCode"], "KeyCode");
        Equal((int)VirtualKey.Modifiers, recorded["Modifiers"], "Modifiers");
        Equal((int)VirtualKey.Shift, recorded["Shift"], "Shift");
        Equal((int)VirtualKey.Control, recorded["Control"], "Control");
        Equal((int)VirtualKey.Alt, recorded["Alt"], "Alt");
    }

    // What the key events of WinForms gave as KeyData (a key code and the modifier bits) is what a KeyStroke reads: for every key code with every
    // set of modifiers the code, the modifiers and the data come back as they were put in.
    private static void KeyStrokeChecks()
    {
        RecordedKeys recorded = ReadKeys();
        int keyCodeMask = recorded["KeyCode"];
        int combinations = 0;
        foreach (string[] modifierNames in new[] { new string[0], new[] { "Shift" }, new[] { "Control" }, new[] { "Alt" }, new[] { "Shift", "Control" }, new[] { "Control", "Alt" }, new[] { "Shift", "Alt" }, new[] { "Shift", "Control", "Alt" } })
        {
            int modifiers = modifierNames.Aggregate(0, (bits, name) => bits | recorded[name]);
            for (int code = 0; code <= 255; code++)
            {
                KeyStroke stroke = new KeyStroke((VirtualKey)(code | modifiers));
                if ((int)stroke.KeyData != (code | modifiers) || (int)stroke.KeyCode != ((code | modifiers) & keyCodeMask)
                    || stroke.Shift != modifierNames.Contains("Shift") || stroke.Control != modifierNames.Contains("Control") || stroke.Alt != modifierNames.Contains("Alt"))
                {
                    throw new Exception("FAIL: the key data " + (code | modifiers) + " does not give its key code and modifiers");
                }
                combinations++;
            }
        }
        Check(combinations == 8 * 256, "all " + combinations + " key codes with all modifier sets give their code and modifiers");

        KeyStroke left = new KeyStroke((VirtualKey)(recorded["Control"] | recorded["Shift"] | recorded["Left"]));
        Equal(left.KeyData, VirtualKey.Control | VirtualKey.Shift | VirtualKey.Left, "Ctrl+Shift+Left gives the key data");
        Equal(left.KeyCode, VirtualKey.Left, "and the key code");
        Check(left.Control && left.Shift && !left.Alt, "and the modifiers");
        Equal(left.Char, '\0', "a key-down stroke has no character");
        Equal(new KeyStroke((VirtualKey)recorded["A"]), new KeyStroke(VirtualKey.A), "a plain key");
        KeyStroke alt = new KeyStroke((VirtualKey)(recorded["Alt"] | recorded["F4"]));
        Check(alt.Alt && alt.KeyCode == VirtualKey.F4, "Alt+F4");
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
        KeyStrokeChecks();
        NamedColorChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " model interop checks passed.");
    }
}
