using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SmarTTY.Terminal;

// The private modes of the terminal (Terminal/PrivateModeTable.cs). The number in CSI ? Pn h / l / s / r comes from the output of the remote
// side and can be negative: "ESC [ ? -1 h" reached _privateModes[-1], threw IndexOutOfRangeException out of the processing of the output,
// and the window "An unexpected error occurred" came up each time the sequence was printed again (cat of a file, a hostile host, noise on
// a serial line). Run with: dotnet run --project tests/PrivateModeTableTests
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
        int[] outside = { -1, -2, -1100, -1101, int.MinValue, int.MinValue + 1, 1100, 1101, int.MaxValue - 1, int.MaxValue };
        Check(outside.All(mode => !PrivateModeTable.IsKept(mode)), "kept: a negative number and a number from 1100 up are not kept");
        Check(PrivateModeTable.IsKept(0) && PrivateModeTable.IsKept(1) && PrivateModeTable.IsKept(1049) && PrivateModeTable.IsKept(1099), "kept: 0 to 1099 are kept");

        PrivateModeTable table = new PrivateModeTable();
        Check(!table.IsSet(25) && !table.IsSet(1049) && !table.IsSet(0), "a new table has no mode set");
        table.Set(25, true);
        table.Set(1049, true);
        Check(table.IsSet(25) && table.IsSet(1049) && !table.IsSet(1048), "set: a mode that is kept is set, the ones next to it are not");
        table.Set(1049, false);
        Check(!table.IsSet(1049), "set: and reset");

        // The sequences of the report: none of them may throw, whatever the table.
        foreach (int mode in outside)
        {
            try
            {
                table.Set(mode, true);
                table.Set(mode, false);
                table.Save(mode);
                bool restored = table.TryGetSaved(mode, out bool value);
                bool set = table.IsSet(mode);
                if (restored || value || set)
                {
                    throw new Exception("a mode that is not kept took a value: " + mode);
                }
            }
            catch (IndexOutOfRangeException)
            {
                throw new Exception("FAIL: mode " + mode + " is a number outside the table and threw IndexOutOfRangeException");
            }
        }
        Check(true, "a number outside the table (negative, 1100, int.MinValue, int.MaxValue) is ignored by Set, Save, TryGetSaved and IsSet");
        Check(Enumerable.Range(0, PrivateModeTable.Size).Where(table.IsSet).SequenceEqual(new[] { 25 }), "the numbers outside the table changed none of the kept ones (only 25 is set)");

        // Save and restore (CSI ? Pn s and CSI ? Pn r).
        PrivateModeTable modes = new PrivateModeTable();
        modes.Set(25, true);
        modes.Save(25);
        modes.Set(25, false);
        Check(modes.TryGetSaved(25, out bool saved) && saved, "restore: the value that was saved comes back");
        Check(!modes.IsSet(25), "restore: reading what was saved does not set the mode (the caller does)");
        Check(modes.TryGetSaved(47, out bool never) && !never, "restore: a mode that was never saved restores as off");
        modes.Save(25);
        Check(modes.TryGetSaved(25, out saved) && !saved, "save: saving again takes the present value");
        modes.Set(1000, true);
        modes.Save(-1);
        modes.Save(1100);
        Check(modes.IsSet(1000) && modes.TryGetSaved(1000, out saved) && !saved, "save: a number outside the table saves nothing and changes nothing");

        // A reset of the terminal.
        modes.Set(1000, true);
        modes.Set(1, true);
        modes.Save(1);
        modes.Clear();
        Check(!modes.IsSet(1) && !modes.IsSet(1000) && modes.TryGetSaved(1, out saved) && !saved, "clear: no mode is set and nothing is saved");

        // Whatever the numbers, never an exception.
        Random random = new Random(7);
        PrivateModeTable fuzzed = new PrivateModeTable();
        for (int round = 0; round < 200000; round++)
        {
            int mode = round % 5 == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(-1500, 2600);
            switch (random.Next(5))
            {
                case 0: fuzzed.Set(mode, random.Next(2) == 0); break;
                case 1: fuzzed.Save(mode); break;
                case 2: fuzzed.TryGetSaved(mode, out _); break;
                case 3: fuzzed.IsSet(mode); break;
                default: if (random.Next(1000) == 0) { fuzzed.Clear(); } break;
            }
        }
        Check(true, "200000 random operations with numbers from int.MinValue to int.MaxValue throw nothing");

        // The emulator reaches the modes only through the table.
        string path = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(item => item.Key == "EmulatorSource").Value;
        string source = File.ReadAllText(path);
        Check(!source.Contains("_privateModes[") && !source.Contains("_savedPrivateModes") && !source.Contains("_privateModes.Length"), "the emulator does not index the modes itself");
        Check(source.Contains("_privateModes.Set(") && source.Contains("_privateModes.Save(") && source.Contains("_privateModes.TryGetSaved(") && source.Contains("_privateModes.IsSet(") && source.Contains("_privateModes.Clear()"),
            "the emulator sets, saves, restores, reads and clears the modes through the table");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
