using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Regression harness for the icons, which are vector images since stage 5A: the DrawingImages of the resource dictionaries in
// GUI/Icons/ (converted from SVG, one for each folder the PNG files were in), found by the path the PNG had through the rules of
// Utilities/IconPaths.cs, which this links. It checks every icon the code names (the {gui:Icon ...} of the XAML, the icon paths
// in the strings of the code, the names the WPF menus ask for through WpfMenus) against the keys of the dictionaries, the rules
// themselves, and that no PNG and no .resx is left. A key that is not there is an empty image at run time, which nothing else
// would show on Linux. Run with: dotnet run --project tests/IconResourceTests
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

    // The top-level arguments of a call whose text starts at start (after its opening parenthesis), with strings and nested
    // parentheses skipped; what follows the closing parenthesis is not read.
    private static List<string> Arguments(string source, int start)
    {
        var arguments = new List<string>();
        int depth = 0;
        int begin = start;
        for (int i = start; i < source.Length; i++)
        {
            char ch = source[i];
            if (ch == '"')
            {
                for (i++; i < source.Length && source[i] != '"'; i++)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }
                }
            }
            else if (ch == '(' || ch == '{' || ch == '[')
            {
                depth++;
            }
            else if (ch == ')' || ch == '}' || ch == ']')
            {
                if (depth == 0)
                {
                    arguments.Add(source.Substring(begin, i - begin));
                    return arguments;
                }
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                arguments.Add(source.Substring(begin, i - begin));
                begin = i + 1;
            }
        }
        return arguments;
    }

    private static string _root;

    private static readonly Dictionary<string, HashSet<string>> _keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    // The keys of the DrawingImages of a dictionary of GUI/Icons/.
    private static HashSet<string> KeysOf(string dictionary)
    {
        if (!_keys.TryGetValue(dictionary, out HashSet<string> keys))
        {
            string path = Path.Combine(_root, "GUI", "Icons", dictionary + ".xaml");
            keys = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(path), "<DrawingImage x:Key=\"([^\"]+)\""))
                {
                    keys.Add(match.Groups[1].Value);
                }
            }
            _keys[dictionary] = keys;
        }
        return keys;
    }

    // An icon path that the code names has a vector image.
    private static void CheckIcon(string path, string where)
    {
        bool parsed = IconPaths.TryParse(path, out string folder, out string name);
        Check(parsed, $"{where}: '{path}' is an icon path");
        if (parsed)
        {
            Check(KeysOf(IconPaths.DictionaryOf(folder)).Contains(IconPaths.KeyOf(name)),
                $"{where}: '{path}' is {IconPaths.KeyOf(name)} of GUI/Icons/{IconPaths.DictionaryOf(folder)}.xaml");
        }
    }

    private static IEnumerable<string> SourceFiles(string pattern, params string[] directories)
    {
        foreach (string directory in directories)
        {
            string path = Path.Combine(_root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
            {
                string sep = Path.DirectorySeparatorChar.ToString();
                if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep) || file.Contains(sep + "Icons" + sep))
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private static void TestRules()
    {
        Check(IconPaths.TryParse("MainWindow/refresh16.png", out string folder, out string name) && folder == "mainwindow" && name == "refresh16",
            "a path under icons/ gives its folder and name");
        Check(IconPaths.TryParse("../icons/net16.png", out folder, out name) && folder == "" && name == "net16", "a relative path of the XAML");
        Check(IconPaths.TryParse("pack://application:,,,/icons/keys/locked.png", out folder, out name) && folder == "keys" && name == "locked",
            "a pack URI");
        Check(IconPaths.TryParse("..\\Icons\\term16.png", out folder, out name) && folder == "" && name == "term16",
            "back slashes and another case of icons");
        Check(IconPaths.TryParse("editor/message-error", out folder, out name) && folder == "editor" && name == "message-error",
            "the extension is optional");
        Check(!IconPaths.TryParse("nosuchfolder/x.png", out _, out _), "a folder that the icons did not have is no icon path");
        Check(!IconPaths.TryParse("", out _, out _) && !IconPaths.TryParse(null, out _, out _), "an empty path is none");
        Check(IconPaths.KeyOf("dialog-scp-download") == "dialog_scp_downloadDrawingImage", "a dash is an underscore in the key");
        Check(IconPaths.KeyOf("FindHS") == "FindHSDrawingImage", "the case of the name is kept in the key");
        Check(IconPaths.SizeOf("", "about") == 128 && IconPaths.SizeOf("mainwindow", "refresh16") == 21.33 && IconPaths.SizeOf("", "delete16") == 16,
            "an icon is shown at the size of its PNG");
        foreach (KeyValuePair<string, string> pair in IconPaths.Dictionaries)
        {
            Check(File.Exists(Path.Combine(_root, "GUI", "Icons", pair.Value + ".xaml")) && KeysOf(pair.Value).Count > 0,
                $"the dictionary of the folder '{pair.Key}' is there with images");
        }
    }

    private static void TestXaml()
    {
        int count = 0;
        foreach (string file in SourceFiles("*.xaml", "GUI").Concat(new[] { Path.Combine(_root, "App.xaml") }))
        {
            if (!File.Exists(file))
            {
                continue;
            }
            string text = File.ReadAllText(file);
            Check(!Regex.IsMatch(text, "\\.png\"", RegexOptions.IgnoreCase), $"{Path.GetFileName(file)} names no PNG file");
            foreach (Match match in Regex.Matches(text, "\\{\\w+:Icon\\s+([^}\\s]+)\\s*\\}"))
            {
                CheckIcon(match.Groups[1].Value, Path.GetFileName(file));
                count++;
            }
        }
        Check(count > 200, "the XAML shows its icons through the Icon extension");
    }

    private static void TestCode()
    {
        int count = 0;
        foreach (string file in SourceFiles("*.cs", "GUI", "Controls"))
        {
            string text = File.ReadAllText(file);
            // The icon paths in the strings: a file name of an icon, maybe with its folder (a file filter like "*.png" is none).
            foreach (Match match in Regex.Matches(text, "\"([A-Za-z0-9_./\\-]+\\.png)\""))
            {
                CheckIcon(match.Groups[1].Value, Path.GetFileName(file));
                count++;
            }
            Check(!text.Contains("pack://application:,,,/icons"), $"{Path.GetFileName(file)} loads no PNG of icons/");
        }
        Check(count > 40, "the code names its icons by their paths");

        // The names of the WPF menus: the icon argument of each call that takes one, mapped by the table of WpfMenus.
        string menus = File.ReadAllText(Path.Combine(_root, "GUI", "WpfMenus.cs"));
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match entry in Regex.Matches(menus, "\\{ \"([a-z0-9-]+)\", \"([A-Za-z0-9_/-]+)\" \\}"))
        {
            table[entry.Groups[1].Value] = entry.Groups[2].Value;
        }
        Check(table.Count > 20, "WpfMenus has the table of its names");
        var iconArguments = new (string Call, int Index)[]
        {
            ("WpfMenus.Icon(", 0), ("WpfMenus.LoadIcon(", 0), ("WpfMenus.Item(", 1), ("new DeviceMenuItem(", 1),
            ("ImageButton(", 0), ("ImageAndText(", 0)
        };
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles("*.cs", "Controls", "GUI", "CodeEditing", "VisualTTY", "Terminal"))
        {
            string source = File.ReadAllText(file);
            foreach ((string call, int index) in iconArguments)
            {
                for (int at = source.IndexOf(call, StringComparison.Ordinal); at >= 0; at = source.IndexOf(call, at + 1, StringComparison.Ordinal))
                {
                    // A declaration ("Button ImageButton(string icon, ...") is no call.
                    List<string> arguments = Arguments(source, at + call.Length);
                    if (arguments.Count <= index || arguments[index].Contains("string "))
                    {
                        continue;
                    }
                    foreach (Match name in Regex.Matches(arguments[index], "\"([a-z0-9-]+)\""))
                    {
                        wanted.Add(name.Groups[1].Value);
                    }
                }
            }
        }
        Check(wanted.Count > 20, "the code asks the menus for named icons");
        foreach (string name in wanted)
        {
            CheckIcon(table.TryGetValue(name, out string path) ? path : "editor/" + name, "the menu icon '" + name + "'");
        }
    }

    private static void TestNoPictures()
    {
        string icons = Path.Combine(_root, "icons");
        Check(!Directory.Exists(icons) || !Directory.EnumerateFiles(icons, "*.png", SearchOption.AllDirectories).Any(), "no PNG is left in icons/");
        string project = File.ReadAllText(Path.Combine(_root, "SmarTTY.csproj"));
        Check(!Regex.IsMatch(project, "\\.png", RegexOptions.IgnoreCase), "SmarTTY.csproj embeds no PNG");
        // The Windows Forms .resx images are gone: the .NET SDK cannot compile them for net48 (README, MSB3822).
        string[] resx = Directory.EnumerateFiles(_root, "*.resx", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Check(resx.Length == 0, "the project has no .resx files");
    }

    private static int Main()
    {
        try
        {
            _root = AppContext.BaseDirectory;
            while (_root != null && !File.Exists(Path.Combine(_root, "SmarTTY.csproj")))
            {
                _root = Path.GetDirectoryName(_root);
            }
            if (_root == null)
            {
                Console.WriteLine("SKIP: repository root not found, the icons are not checked");
                return 0;
            }
            TestRules();
            TestXaml();
            TestCode();
            TestNoPictures();
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }
}
