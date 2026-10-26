using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Regression harness for the icon resources of the WinForms controls. Controls/Icons.cs loads an image by the name
// "SmarTTY.Icons.<name>.png", and SmarTTY.csproj has to embed that name from icons/**. This checks the two sides
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

            // The names Icons.Load/Icons.Apply ask for, from the code that runs at load time.
            var wanted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string directory in new[] { "Controls", "GUI", "CodeEditing", "Forms", "VisualTTY", "Terminal" })
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
                    foreach (Match match in Regex.Matches(File.ReadAllText(file), "Icons\\.(?:Load|Apply)\\([^,]*,?\\s*\"([a-z0-9-]+)\""))
                    {
                        wanted.Add(match.Groups[1].Value);
                    }
                }
            }
            Check(wanted.Count > 0, "the code asks Icons for named images");

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
                Check(header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G' && width == 16 && height == 16,
                    $"the picture of the '{name}' icon is a 16x16 PNG");
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
