using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Regression harness for the icon resources of the WPF menus and tool bars. GUI/WpfMenus.cs loads an image by the name
// "SmarTTY.Icons.<name>.png" (the WinForms Controls/Icons did the same until stage 4), and SmarTTY.csproj has to embed that name from icons/**. This checks the two sides
// still agree, that the picture exists, and that no .resx (whose binary images the .NET SDK cannot compile for
// net48, see README) came back. Run with: dotnet run --project tests/IconResourceTests
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

    private static int Main()
    {
        try
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "SmarTTY.csproj")))
            {
                root = Path.GetDirectoryName(root);
            }
            if (root == null)
            {
                Console.WriteLine("SKIP: repository root not found, icon resources are not checked");
                return 0;
            }

            string project = File.ReadAllText(Path.Combine(root, "SmarTTY.csproj"));

            // The names the WPF menus and tool bars ask for (GUI/WpfMenus: SmarTTY.Icons.<name>.png): the argument that is the
            // icon of each call that takes one. Both names of a "condition ? a : b" argument count.
            var iconArguments = new (string Call, int Index)[]
            {
                ("WpfMenus.Icon(", 0), ("WpfMenus.LoadIcon(", 0), ("WpfMenus.Item(", 1), ("new DeviceMenuItem(", 1),
                ("ImageButton(", 0), ("ImageAndText(", 0)
            };
            var wanted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string directory in new[] { "Controls", "GUI", "CodeEditing", "VisualTTY", "Terminal" })
            {
                string path = Path.Combine(root, directory);
                if (!Directory.Exists(path))
                {
                    continue;
                }
                foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                        file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    {
                        continue;
                    }
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
            }
            Check(wanted.Count > 0, "the code asks for named icons");

            // A few names are pictures rather than toolbar/menu icons: they are shown in a PictureBox at a larger size,
            // and the "connection closed" card reuses the very same picture as the prompt of an SSH session. For those the
            // picture has to be square and at least as large as the 16x16 icons, not exactly 16x16.
            var pictures = new HashSet<string>(StringComparer.Ordinal) { "busy" };

            // Every name must be embedded from a real, 16x16 PNG under icons/.
            // SmarTTY.csproj uses both explicit entries and wildcards (icons\editor\*.png -> SmarTTY.Icons.%(Filename).png).
            var embedded = new Dictionary<string, string>(StringComparer.Ordinal); // logical name -> relative path
            foreach (Match entry in Regex.Matches(project, "<EmbeddedResource Include=\"([^\"]+)\" LogicalName=\"SmarTTY\\.Icons\\.([^\"]+)\""))
            {
                string include = entry.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
                string logical = entry.Groups[2].Value;
                if (include.Contains('*'))
                {
                    string directory = Path.GetDirectoryName(include);
                    foreach (string file in Directory.EnumerateFiles(Path.Combine(root, directory), Path.GetFileName(include)))
                    {
                        embedded[logical.Replace("%(Filename)", Path.GetFileNameWithoutExtension(file))] = Path.Combine(directory, Path.GetFileName(file));
                    }
                }
                else
                {
                    embedded[logical] = include;
                }
            }

            foreach (string name in wanted)
            {
                Check(embedded.TryGetValue(name + ".png", out string relative),
                    $"SmarTTY.csproj embeds the '{name}' icon");
                string picture = Path.Combine(root, relative);
                Check(File.Exists(picture), $"the picture of the '{name}' icon exists ({relative})");
                byte[] header = new byte[24];
                using (FileStream stream = File.OpenRead(picture))
                {
                    stream.Read(header, 0, header.Length);
                }
                int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                bool isPng = header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G';
                if (pictures.Contains(name))
                {
                    Check(isPng && width == height && width >= 16,
                        $"the '{name}' picture is a square PNG of at least 16x16");
                }
                else
                {
                    Check(isPng && width == 16 && height == 16,
                        $"the picture of the '{name}' icon is a 16x16 PNG");
                }
            }

            // The Windows Forms .resx images are gone: the .NET SDK cannot compile them for net48 (README, MSB3822).
            string[] resx = Directory.EnumerateFiles(root, "*.resx", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();
            Check(resx.Length == 0, "the project has no .resx files");

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
