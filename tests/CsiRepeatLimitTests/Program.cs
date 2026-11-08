using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using SmarTTY.Terminal;

// The counts of the CSI commands (Terminal/CsiRepeatLimit.cs). They come from the output of the remote side, up to int.MaxValue:
// "ESC [ 2000000000 L" built a string of two billion line breaks, "ESC [ 2000000000 I" looped that many times on the UI thread.
// Run with: dotnet run --project tests/CsiRepeatLimitTests
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
        const int Rows = 24;
        const int Columns = 80;
        int[] huge = { 100000, 65536, 1 << 20, 100000000, 300000000, 1999999999, 2000000000, int.MaxValue - 1, int.MaxValue };

        // Lines: the number of rows.
        foreach (char command in "ABEFLMST")
        {
            Check(huge.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == Rows), "'" + command + "': any count from 100000 to int.MaxValue is the number of rows");
            Check(new[] { 1, 2, 10, 23, 24 }.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == count), "'" + command + "': a count that the screen holds is left as it is");
            Check(CsiRepeatLimit.Clamp(command, 25, Rows, Columns) == Rows, "'" + command + "': one more than the rows is the rows");
        }

        // Columns: inserting characters and the tabs.
        foreach (char command in "@IZ")
        {
            Check(huge.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == Columns), "'" + command + "': any count from 100000 to int.MaxValue is the number of columns");
            Check(new[] { 1, 8, 79, 80 }.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == count), "'" + command + "': a count that a line holds is left as it is");
        }

        // The cursor to the right and to the left: in byte mode the count is bytes, up to four for a column.
        foreach (char command in "CD")
        {
            Check(huge.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == Columns * 4), "'" + command + "': any count from 100000 to int.MaxValue is four times the columns");
            Check(new[] { 1, 80, 120, 240, 320 }.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == count), "'" + command + "': the bytes of 40 three-byte characters (120) and the like are left as they are");
        }

        // REP fills the screen at most.
        Check(huge.All(count => CsiRepeatLimit.Clamp('b', count, Rows, Columns) == Rows * Columns), "'b': any count from 100000 to int.MaxValue is the number of cells of the screen");
        Check(new[] { 1, 79, 80, 1000, 1920 }.All(count => CsiRepeatLimit.Clamp('b', count, Rows, Columns) == count), "'b': a count that the screen holds is left as it is");

        // A count of 0 or less is 1 (the caller turns 0 into 1 already; the limit never gives less).
        foreach (char command in "ABCDEFILMSTZb@")
        {
            Check(new[] { 0, -1, int.MinValue }.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == 1), "'" + command + "': 0 and below are 1");
        }

        // The commands that are not limited here are returned as they are.
        foreach (char command in "G`dPXHfJKmhlnc")
        {
            Check(new[] { 1, 500, int.MaxValue }.All(count => CsiRepeatLimit.Clamp(command, count, Rows, Columns) == count), "'" + command + "': not limited here");
        }

        // A window of no size, and one of an absurd size.
        Check(CsiRepeatLimit.Clamp('L', int.MaxValue, 0, 0) == 1 && CsiRepeatLimit.Clamp('@', int.MaxValue, -5, -5) == 1 && CsiRepeatLimit.Clamp('b', int.MaxValue, 0, 80) == 80, "a window with no rows or columns still gives 1 or more");
        Check(CsiRepeatLimit.Clamp('b', int.MaxValue, 50000, 50000) == CsiRepeatLimit.MaxCount, "'b': the product of a huge window does not overflow, and the absolute limit holds");
        Check(CsiRepeatLimit.Clamp('C', int.MaxValue, 10, 2000000000) == CsiRepeatLimit.MaxCount, "'C': four times the columns does not overflow either");

        // For any command and any numbers: 1 or more, never more than the count asked for (when that is 1 or more) or than the absolute limit,
        // and the same again when it is applied twice.
        Random random = new Random(11);
        bool sound = true;
        string bad = null;
        for (int round = 0; round < 300000; round++)
        {
            char command = "ABCDEFGILMSTZbdPX@`"[random.Next(19)];
            int rows = random.Next(-2, 400);
            int columns = random.Next(-2, 800);
            int count = round % 4 == 0 ? random.Next(int.MinValue, int.MaxValue) : random.Next(0, 5000);
            int clamped = CsiRepeatLimit.Clamp(command, count, rows, columns);
            bool limited = "ABCDEFILMSTZb@".IndexOf(command) >= 0;
            bool ok = limited
                ? clamped >= 1 && clamped <= CsiRepeatLimit.MaxCount && (count < 1 || clamped <= count) && CsiRepeatLimit.Clamp(command, clamped, rows, columns) == clamped
                : clamped == count;
            if (!ok)
            {
                sound = false;
                bad = bad ?? command + " " + count + " in " + rows + "x" + columns + " gave " + clamped;
            }
        }
        Check(sound, "300000 random commands, counts and window sizes: 1 or more, within the count and the absolute limit, and unchanged by a second pass" + (bad == null ? "" : " (first bad: " + bad + ")"));

        // What the limit is for: nothing the emulator builds from a count gets large.
        Stopwatch watch = Stopwatch.StartNew();
        long characters = 0;
        foreach (char command in "ABCDEFILMSTZb@")
        {
            characters += CsiRepeatLimit.Clamp(command, int.MaxValue, 100, 300);
        }
        watch.Stop();
        Check(characters <= 14L * CsiRepeatLimit.MaxCount && watch.ElapsedMilliseconds < 1000, "the counts of all the commands together stay small (" + characters + " in all)");

        // The emulator applies the limit to the count before any command is carried out.
        string path = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(item => item.Key == "EmulatorSource").Value;
        string source = File.ReadAllText(path);
        int limitAt = source.IndexOf("repeatCount = CsiRepeatLimit.Clamp(ch, repeatCount, _visibleRows, _visibleColumns);", StringComparison.Ordinal);
        int switchAt = source.IndexOf("switch (ch)", Math.Max(limitAt, 0), StringComparison.Ordinal);
        int declaredAt = source.IndexOf("int repeatCount = 1;", StringComparison.Ordinal);
        Check(declaredAt >= 0 && limitAt > declaredAt && switchAt > limitAt, "the emulator limits the count after it is read and before the commands are carried out");
        Check(source.IndexOf("CsiRepeatLimit.Clamp(", limitAt + "repeatCount = CsiRepeatLimit.Clamp(".Length, StringComparison.Ordinal) < 0, "and only once");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " checks passed.");
        return 0;
    }
}
