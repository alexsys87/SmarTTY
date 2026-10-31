using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.CodeEditing;

// Regression harness for the Text menu of the editor: the functions that rewrite lines (trailing blanks, sorting, duplicates,
// the blanks that indent a line). Run with: dotnet run --project tests/LineTransformTests
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

    private static void Same(string[] actual, string[] expected, string name)
    {
        bool equal = actual.SequenceEqual(expected);
        Check(equal, name + (equal ? "" : " (expected [" + string.Join("|", expected) + "], got [" + string.Join("|", actual) + "])"));
    }

    private static string[] Lines(params string[] lines)
    {
        return lines;
    }

    private static void TrimChecks()
    {
        Same(LineTransforms.TrimTrailingWhitespace(Lines("a  ", "b\t", "c \t ", "  d", "", "e")), Lines("a", "b", "c", "  d", "", "e"), "trailing spaces and tabs go, leading ones stay");
        Same(LineTransforms.Apply(LineTransformKind.TrimTrailingWhitespace, Lines("x \u00a0"), 4), Lines("x \u00a0"), "only spaces and tabs are trimmed (a no-break space is text)");
        Same(LineTransforms.Apply(LineTransformKind.TrimTrailingWhitespace, new string[0], 4), new string[0], "no lines, no change");
        Same(LineTransforms.Apply(LineTransformKind.TrimTrailingWhitespace, null, 4), new string[0], "null is no lines");
    }

    private static void SortChecks()
    {
        Same(LineTransforms.Sort(Lines("pear", "Apple", "banana", "apple"), false), Lines("Apple", "apple", "banana", "pear"), "ascending ignores case; equal letters: upper case first");
        Same(LineTransforms.Sort(Lines("pear", "Apple", "banana", "apple"), true), Lines("pear", "banana", "apple", "Apple"), "descending is the exact reverse");
        Same(LineTransforms.Sort(Lines("b", "", "a", ""), false), Lines("", "", "a", "b"), "empty lines come first");
        Same(LineTransforms.Sort(Lines("10", "9", "100"), false), Lines("10", "100", "9"), "text order, not numbers");
        Same(LineTransforms.Sort(Lines("  b", "a", " c"), false), Lines("  b", " c", "a"), "leading blanks count (space sorts before letters)");
        Same(LineTransforms.Sort(Lines("same", "same"), false), Lines("same", "same"), "equal lines stay");
        Same(LineTransforms.Sort(new string[0], false), new string[0], "no lines");
        Same(LineTransforms.Apply(LineTransformKind.SortAscending, Lines("b", "a"), 4), Lines("a", "b"), "Apply: ascending");
        Same(LineTransforms.Apply(LineTransformKind.SortDescending, Lines("a", "b"), 4), Lines("b", "a"), "Apply: descending");

        // The sort does not depend on the culture of the machine.
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Same(LineTransforms.Sort(Lines("I", "i", "h", "j"), false), Lines("h", "I", "i", "j"), "the Turkish culture does not change the order");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    private static void DuplicateChecks()
    {
        Same(LineTransforms.RemoveDuplicates(Lines("a", "b", "a", "c", "b", "a")), Lines("a", "b", "c"), "the first of each stays, in its place");
        Same(LineTransforms.RemoveDuplicates(Lines("a", "A", "a ")), Lines("a", "A", "a "), "case and trailing blanks make lines different");
        Same(LineTransforms.RemoveDuplicates(Lines("", "x", "", "")), Lines("", "x"), "empty lines are duplicates of each other");
        Same(LineTransforms.RemoveDuplicates(new string[0]), new string[0], "no lines");
        Same(LineTransforms.Apply(LineTransformKind.RemoveDuplicateLines, Lines("1", "1"), 4), Lines("1"), "Apply: duplicates");
    }

    private static void IndentationChecks()
    {
        Check(LineTransforms.IndentationToSpaces("\tfoo", 4) == "    foo", "a tab is tabSize spaces");
        Check(LineTransforms.IndentationToSpaces("\t\tfoo", 2) == "    foo", "two tabs, tabSize 2");
        Check(LineTransforms.IndentationToSpaces(" \tfoo", 4) == "    foo", "a space and a tab reach the first tab stop");
        Check(LineTransforms.IndentationToSpaces("  \t foo", 4) == "     foo", "two spaces, a tab, a space: 5 columns");
        Check(LineTransforms.IndentationToSpaces("\tfoo\tbar", 4) == "    foo\tbar", "only the indent changes, a tab inside the line stays");
        Check(LineTransforms.IndentationToSpaces("foo\t", 4) == "foo\t", "no indent, no change");
        Check(LineTransforms.IndentationToSpaces("    foo", 4) == "    foo", "spaces already");
        Check(LineTransforms.IndentationToSpaces("\t", 4) == "    ", "a line of only a tab becomes spaces");

        Check(LineTransforms.IndentationToTabs("        foo", 4) == "\t\tfoo", "8 spaces are two tabs");
        Check(LineTransforms.IndentationToTabs("      foo", 4) == "\t  foo", "6 spaces: a tab and 2 spaces");
        Check(LineTransforms.IndentationToTabs("   foo", 4) == "   foo", "less than a tab stays spaces");
        Check(LineTransforms.IndentationToTabs("  \t  foo", 4) == "\t  foo", "mixed blanks: 6 columns");
        Check(LineTransforms.IndentationToTabs("foo        bar", 4) == "foo        bar", "spaces inside the line stay");
        Check(LineTransforms.IndentationToTabs("", 4) == "", "empty line");
        Check(LineTransforms.IndentationToTabs("\tfoo", 4) == "\tfoo", "tabs already");

        // Both ways give the same text back for an indent that is a whole number of tabs.
        string[] indented = Lines("\t\tdeep", "\tone", "none", "\t\t\tthree");
        Same(LineTransforms.Apply(LineTransformKind.IndentationToTabs, LineTransforms.Apply(LineTransformKind.IndentationToSpaces, indented, 8), 8), indented, "to spaces and back, tab size 8");

        // The tab size is kept to something sensible.
        Check(LineTransforms.Apply(LineTransformKind.IndentationToSpaces, Lines("\tx"), 0)[0] == " x", "tab size 0 is read as 1");
        Check(LineTransforms.Apply(LineTransformKind.IndentationToSpaces, Lines("\tx"), 1000)[0] == new string(' ', LineTransforms.MaxTabSize) + "x", "a huge tab size is cut");
    }

    private static void KindChecks()
    {
        Check(LineTransforms.NeedsSelection(LineTransformKind.SortAscending) && LineTransforms.NeedsSelection(LineTransformKind.SortDescending) && LineTransforms.NeedsSelection(LineTransformKind.RemoveDuplicateLines), "sorting and duplicates need lines selected");
        Check(!LineTransforms.NeedsSelection(LineTransformKind.TrimTrailingWhitespace) && !LineTransforms.NeedsSelection(LineTransformKind.IndentationToSpaces) && !LineTransforms.NeedsSelection(LineTransformKind.IndentationToTabs) && !LineTransforms.NeedsSelection(LineTransformKind.EnsureFinalNewline), "the others work on the whole text");
        Same(LineTransforms.Apply(LineTransformKind.EnsureFinalNewline, Lines("a", "b"), 4), Lines("a", "b"), "the final newline is the editor's: the lines are not changed");
        HashSet<string> titles = new HashSet<string>();
        foreach (LineTransformKind kind in Enum.GetValues(typeof(LineTransformKind)))
        {
            titles.Add(LineTransforms.Title(kind));
        }
        Check(titles.Count == Enum.GetValues(typeof(LineTransformKind)).Length, "every command has a title of its own");
        bool threw = false;
        try
        {
            LineTransforms.Title((LineTransformKind)999);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        Check(threw, "an unknown command has no title");
    }

    private static void Main()
    {
        TrimChecks();
        SortChecks();
        DuplicateChecks();
        IndentationChecks();
        KindChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " line transform checks passed.");
    }
}
