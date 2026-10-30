using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Regression harness for the encoding of the XAML files. The XAML compiler on Windows reads a .xaml file that has
// no UTF-8 BOM in the system ANSI code page (CP1252/CP1251), ignoring the encoding="utf-8" of the XML declaration.
// A non-ASCII character then turns into two garbage characters in the compiled window: in AboutWindow.xaml the
// copyright sign and the "U" with diaeresis of "Sysprogs OU" showed as mojibake after "Sysprogs". Every XAML file
// of the project carries a BOM, so this checks that none loses it. Run with: dotnet run --project tests/XamlEncodingTests
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
                Console.WriteLine("SKIP: repository root not found, the XAML encoding is not checked");
                return 0;
            }

            var offenders = new List<string>();
            int scanned = 0;
            foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                {
                    continue;
                }
                scanned++;
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    continue; // the BOM keeps the file UTF-8 for the XAML compiler on any code page
                }
                if (!HasNonAscii(bytes))
                {
                    continue; // an all-ASCII file is decoded the same way on every code page
                }
                offenders.Add(Path.GetRelativePath(root, file));
            }

            Check(scanned > 0, "the project has XAML files to check");
            Check(offenders.Count == 0,
                "every XAML file with non-ASCII characters has a UTF-8 BOM" +
                (offenders.Count == 0 ? "" : " (missing: " + string.Join(", ", offenders) + ")"));

            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
    }

    private static bool HasNonAscii(byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b > 0x7F)
            {
                return true;
            }
        }
        return false;
    }
}
