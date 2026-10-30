using System;
using System.IO;
using System.Text.RegularExpressions;

// Regression harness for the layout of GUI/PublicKeyManagerWindow.xaml. The window shows the generated public key,
// whose length depends on the algorithm (a 4096-bit RSA key is much longer than an Ed25519 one). While the window
// had a fixed height and the key sat in a star row with a minimum height, a long key was clipped into a small
// scrolling box instead of the window fitting it. The window now grows to the key (SizeToContent="Height") and is
// capped to the screen in code. Run with: dotnet run --project tests/PublicKeyWindowLayoutTests
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
                Console.WriteLine("SKIP: repository root not found, the public key window layout is not checked");
                return 0;
            }

            string xaml = File.ReadAllText(Path.Combine(root, "GUI", "PublicKeyManagerWindow.xaml"));

            string window = Regex.Match(xaml, "<Window\\b[^>]*>").Value;
            Check(window.Length > 0, "the public key window element is found");
            Check(window.Contains("SizeToContent=\"Height\""),
                "the window grows to fit the public key it shows");
            Check(!Regex.IsMatch(window, "Height=\"[0-9]+\""),
                "the window no longer has a fixed height that clips a long key");

            // The key box must not carry a minimum height that hides the difference between a short and a long
            // key; with the window sized to its content the box is exactly as tall as the key.
            string keyBox = Regex.Match(xaml, "<TextBox\\b[^>]*Name=\"PublicKeyText\"[^>]*/>").Value;
            Check(keyBox.Length > 0, "the public key text box is found");
            Check(keyBox.Contains("AcceptsReturn=\"True\"") && keyBox.Contains("TextWrapping=\"Wrap\""),
                "the key box wraps the key over several lines");
            Check(!keyBox.Contains("MinHeight"),
                "the key box is as tall as the key, not a fixed minimum");

            // A very long key (or a short display) must still leave the buttons reachable, so the height is capped.
            string code = File.ReadAllText(Path.Combine(root, "GUI", "PublicKeyManagerWindow.xaml.cs"));
            Check(code.Contains("MaxHeight = SystemParameters.WorkArea.Height"),
                "the window height is capped to the screen work area");
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }
}
