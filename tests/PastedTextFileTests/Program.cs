using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmarTTY.Utilities;

// The name of the file that a large paste is sent as (Utilities/PastedTextFile.cs). Two pastes in the same second used to get the same
// name: the second wrote over the local file that the first upload was still to read, and replaced the first file on the server.
// Run with: dotnet run --project tests/PastedTextFileTests
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
        DateTime now = new DateTime(2026, 11, 7, 23, 59, 58);
        string name = PastedTextFile.NewName(now);
        Check(Regex.IsMatch(name, "^paste-20261107-235958-[0-9a-f]{12}\\.txt$"), "name: the time, twelve characters and .txt (" + name + ")");
        HashSet<string> names = new HashSet<string>();
        for (int index = 0; index < 20000; index++)
        {
            names.Add(PastedTextFile.NewName(now));
        }
        Check(names.Count == 20000, "name: 20000 pastes in the same second all get another name");
        Check(names.All(item => Regex.IsMatch(item, "^[A-Za-z0-9._-]+$")), "name: letters, digits, dot, minus and underscore only (the same name on every system)");
        Check(names.All(item => item.Length < 40), "name: short");
        Check(PastedTextFile.NewName(new DateTime(2026, 1, 2, 3, 4, 5)).StartsWith("paste-20260102-030405-"), "name: the time sorts the files in the order of the pastes");
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Check(PastedTextFile.NewName(now).StartsWith("paste-20261107-235958-"), "name: the same digits whatever the culture of the user (a calendar of another kind, other digits)");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
