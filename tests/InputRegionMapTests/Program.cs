using System;
using System.Collections.Generic;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;

// Checks of the mapping between the text of a command line that is edited in place and the positions in the document (Terminal/InputRegionMap.cs):
// the text of the region, the index of a position, and the position of an index, over one row and over several.
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
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static EditableTextDocument Document(params string[] lines)
    {
        return new EditableTextDocument(lines, TextDocument.LineEnding.Lf);
    }

    private static string Pos(TextDocument.TextPosition position) => position.Column + "," + position.Line;

    private static void TextChecks()
    {
        EditableTextDocument document = Document("output", "user@host:~$ ls -l");
        Equal(InputRegionMap.GetText(document, 1, 13), "ls -l", "the text starts after the prompt, on the line of the region");
        Equal(InputRegionMap.GetText(document, 1, 18), "", "an empty region at the end of the line");
        document = Document("old", "user@host:~$ aaaa", "bbbb", "cc");
        Equal(InputRegionMap.GetText(document, 1, 13), "aaaabbbbcc", "the rows of a wrapped line are one text");
        Equal(InputRegionMap.GetText(document, 2, 0), "bbbbcc", "from another line");
        Equal(InputRegionMap.GetText(document, 3, 1), "c", "a column of the first line");
        Equal(InputRegionMap.GetText(Document("x"), 0, 0), "x", "the whole document");
    }

    private static void IndexChecks()
    {
        EditableTextDocument document = Document("old", "user@host:~$ aaaa", "bbbb", "cc");
        // The region starts at column 13 of line 1: "aaaa" is 13..17, "bbbb" is the next row, "cc" the last.
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(13, 1)), 0, "the start of the region is 0");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(15, 1)), 2, "a place in the first row");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(17, 1)), 4, "the end of the first row");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(0, 2)), 4, "the start of the next row is the same index as the end of the one before");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(3, 2)), 7, "a place in the second row");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(2, 3)), 10, "the end of the text");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(5, 0)), -1, "a line above the region has no index");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(0, 9)), -1, "nor has a line past the document");
        Equal(InputRegionMap.IndexOf(document, 1, 13, new TextDocument.TextPosition(5, 1)), -8, "a place in the prompt is before the start (negative)");
    }

    private static void PositionChecks()
    {
        EditableTextDocument document = Document("old", "user@host:~$ aaaa", "bbbb", "cc");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 0)), "13,1", "index 0 is the start of the region");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 2)), "15,1", "a place in the first row");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 4)), "17,1", "the end of a row is on that row (the same index as the start of the next)");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 5)), "1,2", "the next index is in the next row");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 8)), "4,2", "the end of the second row");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 10)), "2,3", "the end of the text");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, 11)), "2,3", "an index past the text is the end of the document");
        Equal(Pos(InputRegionMap.PositionAt(document, 1, 13, -1)), "2,3", "a negative index is the end of the document");
        Equal(Pos(InputRegionMap.PositionAt(Document(), 0, 0, 3)), "0,0", "an empty document: its start");
        Equal(Pos(InputRegionMap.PositionAt(Document("", "ab"), 1, 0, 1)), "1,1", "a region at the start of a line");

        // The two ways agree wherever a row does not end.
        bool agree = true;
        for (int index = 0; index <= 10; index++)
        {
            TextDocument.TextPosition position = InputRegionMap.PositionAt(document, 1, 13, index);
            if (InputRegionMap.IndexOf(document, 1, 13, position) != index)
            {
                agree = false;
            }
        }
        Check(agree, "the index of the position of an index is the index");
    }

    private static void Main()
    {
        TextChecks();
        IndexChecks();
        PositionChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " input region map checks passed.");
    }
}
