using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;

// Regression harness for the column of "file:line:column" messages: which character of the line it points at when it counts screen
// columns with tab stops (gcc), bytes (clang, go) or characters, and which unit the command line says.
// Run with: dotnet run --project tests/CompilerColumnTests
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
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static int Index(string line, int column, ColumnUnit unit, int tabStop = CompilerColumns.DefaultTabStop)
    {
        return CompilerColumns.ToCharIndex(line, column, unit, tabStop);
    }

    private static void TabStopChecks()
    {
        const ColumnUnit U = ColumnUnit.TabStops;
        // gcc: "\t\tfoo(x)" is "foo" from the screen column 17 (16 from 0).
        Equal(Index("\t\tfoo(x)", 16, U), 2, "two tabs: the screen column 16 is the 'f' at index 2");
        Equal(Index("\t\tfoo(x)", 18, U), 4, "...and 18 is the second 'o'");
        Equal(Index("\tint x;", 8, U), 1, "one tab: the screen column 8 is the 'i' at index 1");
        Equal(Index("\tint x;", 13, U), 6, "the ';' after a tab and 'int x'");
        Equal(Index("  \tx", 8, U), 3, "two spaces and a tab reach the first tab stop, so 'x' is at the screen column 8");
        Equal(Index("   \t  x", 10, U), 6, "three spaces, a tab (to 8), two spaces: 'x' is at the screen column 10");
        Equal(Index("\tx", 3, U), 0, "a column inside the width of a tab is the tab");
        Equal(Index("\tx", 7, U), 0, "...also its last column");
        Equal(Index("\tab", 10, U), 3, "the end of the line (just after the last letter) is the length of the line");
        Equal(Index("\tab", 50, U), 3, "past the end: the length of the line");
        Equal(Index("\tx", 4, U, 4), 1, "-ftabstop=4: the tab is 4 wide");
        Equal(Index("a\tb", 8, U), 2, "a tab after a letter goes to the next stop: 'b' is at the screen column 8");
        Equal(Index("foo bar", 4, U), 4, "no tab, no change");
        Equal(Index("foo", 0, U), 0, "the first column");

        // Since gcc 11 a letter counts as wide as it is shown.
        Equal(Index("Привет x", 7, U), 7, "Cyrillic letters are one column each");
        Equal(Index("日本x", 4, U), 2, "wide letters (CJK) are two columns each");
        Equal(Index("éx", 1, U), 2, "an accent takes no column: 'x' is at the column 1");
        Equal(Index("a\U0001F600b", 3, U), 3, "an emoji is two columns (and two UTF-16 units)");
    }

    private static void ByteChecks()
    {
        const ColumnUnit U = ColumnUnit.Bytes;
        Equal(Index("\tint x", 5, U), 5, "bytes: a tab is one byte, so the byte 5 is the 'x'");
        Equal(Index("\t\tfoo", 2, U), 2, "bytes: after two tabs");
        Equal(Index("Привет x", 13, U), 7, "bytes: 6 Cyrillic letters (2 bytes each), a space: the 'x' is at the byte 13");
        Equal(Index("Привет x", 3, U), 1, "bytes: a byte in the middle of a letter is that letter");
        Equal(Index("a€b", 4, U), 2, "bytes: the euro sign is 3 bytes");
        Equal(Index("a\U0001F600b", 5, U), 3, "bytes: an emoji is 4 bytes (2 UTF-16 units)");
        Equal(Index("abc", 99, U), 3, "bytes: past the end");
    }

    private static void CharacterChecks()
    {
        const ColumnUnit U = ColumnUnit.Characters;
        Equal(Index("\tint x", 5, U), 5, "characters: a tab is one");
        Equal(Index("abc", 2, U), 2, "characters: the index itself");
        Equal(Index("abc", 3, U), 3, "characters: the end");
        Equal(Index("abc", 9, U), 3, "characters: past the end");
        Equal(Index("a\U0001F600b", 3, U), 3, "characters: UTF-16 units, as the editor counts");
        Equal(Index("abc", 2, ColumnUnit.Auto), 2, "Auto that was never resolved is read as characters");
        Equal(Index(null, 5, U), 0, "no line");
        Equal(Index("", 5, U), 0, "an empty line");
        Equal(Index("abc", -3, U), 0, "a negative column");
        Equal(Index("\tx", 4, ColumnUnit.TabStops, 0), 0, "a tab stop of 0 is read as 8: the tab is 8 wide, so the column 4 is in the tab");
        Equal(Index("\tx", 8, ColumnUnit.TabStops, 0), 1, "...and the column 8 is the 'x'");
    }

    private static void ResolveChecks()
    {
        ColumnUnit A = ColumnUnit.Auto;
        foreach (string gcc in new[] { "gcc -Wall main.c", "g++ -c a.cpp", "cc -o a a.c", "c++ x.cc", "gcc-13 a.c", "arm-openwrt-linux-gcc -c x.c",
            "x86_64-linux-gnu-g++-12 a.cpp", "/usr/bin/gcc a.c", "make", "make -C sub", "gmake all", "cmake --build build", "ninja -C out",
            "./configure && make", "sudo make install", "cd src && make V=1", "make 2>&1 | tee build.log", "gfortran a.f90", "meson compile" })
        {
            Equal(CompilerColumns.Resolve(A, gcc), ColumnUnit.TabStops, "Auto: '" + gcc + "' counts tab stops");
        }
        foreach (string clang in new[] { "clang -c a.c", "clang++ a.cpp", "clang-15 a.c", "clang++-15 a.cpp", "/usr/bin/clang a.c", "CC=clang make", "make CC=/usr/bin/clang",
            "make CXX=\"clang++\"", "aarch64-linux-gnu-clang a.c", "clang-tidy a.c", "cmake -DCMAKE_C_COMPILER=clang ." })
        {
            Equal(CompilerColumns.Resolve(A, clang), ColumnUnit.Bytes, "Auto: '" + clang + "' counts bytes");
        }
        foreach (string go in new[] { "go build ./...", "go vet", "go test ./pkg", "cd app && go build" })
        {
            Equal(CompilerColumns.Resolve(A, go), ColumnUnit.Bytes, "Auto: '" + go + "' counts bytes");
        }
        foreach (string other in new[] { "cargo build", "rustc main.rs", "python3 t.py", "ls", "node app.js", "mygcc a.c", "gccx a.c", "gofmt -l .", "docker run go-app", "echo logo", "" , null })
        {
            Equal(CompilerColumns.Resolve(A, other), ColumnUnit.Characters, "Auto: '" + other + "' counts characters");
        }

        // The setting decides when it says anything.
        Equal(CompilerColumns.Resolve(ColumnUnit.Characters, "gcc a.c"), ColumnUnit.Characters, "the setting Characters wins over gcc");
        Equal(CompilerColumns.Resolve(ColumnUnit.Bytes, "make"), ColumnUnit.Bytes, "the setting Bytes wins over make");
        Equal(CompilerColumns.Resolve(ColumnUnit.TabStops, "python3 t.py"), ColumnUnit.TabStops, "the setting TabStops wins over python");
        Equal(CompilerColumns.Resolve(ColumnUnit.TabStops, null), ColumnUnit.TabStops, "...also without a command");
    }

    private static void TextChecks()
    {
        foreach (ColumnUnit unit in Enum.GetValues(typeof(ColumnUnit)))
        {
            object[] attributes = typeof(ColumnUnit).GetField(unit.ToString()).GetCustomAttributes(typeof(SmarTTY.Utilities.EnumDisplayText), false);
            Check(attributes.Length == 1 && ((SmarTTY.Utilities.EnumDisplayText)attributes[0]).Text.Length > 0, unit + " has a text in the settings window");
        }
        Check(Enum.GetValues(typeof(ColumnUnit)).GetValue(0).Equals(ColumnUnit.Auto), "Auto is the default (the first value, 0)");
        Check(CompilerColumns.Width('a') == 1 && CompilerColumns.Width(0x301) == 0 && CompilerColumns.Width(0x65e5) == 2 && CompilerColumns.Width(0x1F600) == 2, "widths: a letter 1, an accent 0, a wide letter and an emoji 2");
    }

    private static void Main()
    {
        TabStopChecks();
        ByteChecks();
        CharacterChecks();
        ResolveChecks();
        TextChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " compiler column checks passed.");
    }
}
