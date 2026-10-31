using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.CodeEditing;

/// <summary>A place in a text: the line and the index of the character in it (both counted from 0).</summary>
public readonly struct TextPoint : IEquatable<TextPoint>, IComparable<TextPoint>
{
    public readonly int Line;

    public readonly int Column;

    public TextPoint(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public bool Equals(TextPoint other) => Line == other.Line && Column == other.Column;

    public override bool Equals(object obj) => obj is TextPoint other && Equals(other);

    public override int GetHashCode() => Line * 397 ^ Column;

    public int CompareTo(TextPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator ==(TextPoint a, TextPoint b) => a.Equals(b);

    public static bool operator !=(TextPoint a, TextPoint b) => !a.Equals(b);

    public static bool operator <(TextPoint a, TextPoint b) => a.CompareTo(b) < 0;

    public static bool operator >(TextPoint a, TextPoint b) => a.CompareTo(b) > 0;

    public static bool operator <=(TextPoint a, TextPoint b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TextPoint a, TextPoint b) => a.CompareTo(b) >= 0;

    public override string ToString() => Line + ":" + Column;
}

/// <summary>One caret and the selection that ends in it (the anchor is where the selection started; no selection when they are the same).</summary>
public struct TextCursor
{
    public TextPoint Anchor;

    public TextPoint Caret;

    /// <summary>The column the caret returns to when it moves up or down over shorter lines; -1 when it has none yet.</summary>
    public int Goal;

    public TextCursor(TextPoint anchor, TextPoint caret, int goal = -1)
    {
        Anchor = anchor;
        Caret = caret;
        Goal = goal;
    }

    public static TextCursor At(TextPoint point) => new TextCursor(point, point);

    public TextPoint Start => Anchor <= Caret ? Anchor : Caret;

    public TextPoint End => Anchor <= Caret ? Caret : Anchor;

    public bool HasSelection => Anchor != Caret;
}

/// <summary>The lines of the text the cursors are in.</summary>
public interface IMultiCursorText
{
    int LineCount { get; }

    string GetLine(int line);
}

/// <summary>Cuts the text between two places out and puts a text in its place (the lines are counted as in the text before the edit).</summary>
public sealed class TextEdit
{
    public TextPoint Start;

    public TextPoint End;

    public string Text;

    public TextEdit(TextPoint start, TextPoint end, string text)
    {
        Start = start;
        End = end;
        Text = text ?? "";
    }
}

/// <summary>
/// The cursors of an editor, in the order of the text and without overlaps: cursors that meet are merged when the set is built.
/// One of them is the primary one (the last that was added or moved), the one the editor's own caret stands for.
/// </summary>
public sealed class CursorSet
{
    /// <summary>More cursors than this are not made (Select all occurrences of a letter in a big file): they would make every key press slow.</summary>
    public const int MaxCursors = 5000;

    public TextCursor[] Items { get; }

    public int Primary { get; }

    /// <summary>
    /// The selection of the primary cursor was made from the word under the caret, so the next occurrences must be whole words too
    /// (Ctrl+D on a word does not select "foobar" when the word is "foo").
    /// </summary>
    public bool WholeWord { get; }

    public int Count => Items.Length;

    public TextCursor PrimaryCursor => Items[Primary];

    private CursorSet(TextCursor[] items, int primary, bool wholeWord)
    {
        Items = items;
        Primary = primary;
        WholeWord = wholeWord;
    }

    public static CursorSet Single(TextCursor cursor) => new CursorSet(new[] { cursor }, 0, false);

    /// <summary>Sorts the cursors, merges those that overlap and tells where the primary one went.</summary>
    public static CursorSet Create(IEnumerable<TextCursor> cursors, int primaryIndex, bool wholeWord = false)
    {
        List<KeyValuePair<TextCursor, bool>> list = new List<KeyValuePair<TextCursor, bool>>();
        int index = 0;
        foreach (TextCursor cursor in cursors)
        {
            list.Add(new KeyValuePair<TextCursor, bool>(cursor, index == primaryIndex));
            index++;
        }
        if (list.Count == 0)
        {
            throw new ArgumentException("A cursor set needs a cursor.", nameof(cursors));
        }
        // Stable: List.Sort is not, and equal cursors must keep their order so that the primary one is found again.
        List<KeyValuePair<TextCursor, bool>> sorted = new List<KeyValuePair<TextCursor, bool>>(list.Count);
        int[] order = new int[list.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) =>
        {
            int result = list[a].Key.Start.CompareTo(list[b].Key.Start);
            if (result == 0)
            {
                result = list[a].Key.End.CompareTo(list[b].Key.End);
            }
            return result != 0 ? result : a.CompareTo(b);
        });
        foreach (int i in order)
        {
            sorted.Add(list[i]);
        }

        List<TextCursor> merged = new List<TextCursor>(sorted.Count);
        int primary = 0;
        foreach (KeyValuePair<TextCursor, bool> item in sorted)
        {
            TextCursor cursor = item.Key;
            if (merged.Count > 0)
            {
                TextCursor last = merged[merged.Count - 1];
                bool touch = cursor.Start < last.End || (cursor.Start == last.End && (!cursor.HasSelection || !last.HasSelection));
                if (touch)
                {
                    TextPoint end = cursor.End > last.End ? cursor.End : last.End;
                    // The merged cursor keeps the direction of the one that reaches further.
                    bool forward = cursor.End > last.End ? cursor.Anchor <= cursor.Caret : last.Anchor <= last.Caret;
                    TextPoint start = last.Start;
                    merged[merged.Count - 1] = forward ? new TextCursor(start, end, -1) : new TextCursor(end, start, -1);
                    if (item.Value)
                    {
                        primary = merged.Count - 1;
                    }
                    continue;
                }
            }
            merged.Add(cursor);
            if (item.Value)
            {
                primary = merged.Count - 1;
            }
        }
        return new CursorSet(merged.ToArray(), primary, wholeWord);
    }

    /// <summary>The same cursors with another primary one.</summary>
    public CursorSet WithPrimary(int primary) => new CursorSet(Items, primary, WholeWord);
}

/// <summary>What typing, deleting or pasting does at several cursors at once: the edits to make, and where the cursors stand afterwards.</summary>
public sealed class EditPlan
{
    /// <summary>In the order of the text, without overlaps, in the coordinates of the text before the edits.</summary>
    public List<TextEdit> Edits = new List<TextEdit>();

    /// <summary>The cursors after the edits, in the coordinates of the text after them.</summary>
    public CursorSet Result;
}

public enum CursorMove
{
    Left,
    Right,
    Up,
    Down,
    Home,
    End,
    WordLeft,
    WordRight
}

/// <summary>
/// Editing with several cursors, free of any UI: every command takes the text and the cursors and returns the new cursors or a plan of
/// edits for the editor to apply (in reverse order, as one undo step). The editor draws the cursors and passes the keys on.
/// </summary>
public static class MultiCursor
{
    public static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private static readonly string[] LineBreaks = { "\r\n", "\n", "\r" };

    public static string[] SplitLines(string text) => text.Split(LineBreaks, StringSplitOptions.None);

    private static string LineOf(IMultiCursorText text, int line) => line >= 0 && line < text.LineCount ? text.GetLine(line) ?? "" : "";

    private static TextPoint Clamp(IMultiCursorText text, TextPoint point)
    {
        int line = Math.Max(0, Math.Min(point.Line, text.LineCount - 1));
        int column = Math.Max(0, Math.Min(point.Column, LineOf(text, line).Length));
        return new TextPoint(line, column);
    }

    /// <summary>Where the caret is after the text has been put at <paramref name="start"/>.</summary>
    public static TextPoint EndOfInsertion(TextPoint start, string text)
    {
        string[] lines = SplitLines(text);
        if (lines.Length == 1)
        {
            return new TextPoint(start.Line, start.Column + lines[0].Length);
        }
        return new TextPoint(start.Line + lines.Length - 1, lines[lines.Length - 1].Length);
    }

    // ---------------------------------------------------------------- moving

    public static CursorSet Move(IMultiCursorText text, CursorSet set, CursorMove move, bool extend)
    {
        TextCursor[] result = new TextCursor[set.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = MoveOne(text, set.Items[i], move, extend);
        }
        return CursorSet.Create(result, set.Primary);
    }

    private static TextCursor MoveOne(IMultiCursorText text, TextCursor cursor, CursorMove move, bool extend)
    {
        TextPoint caret = cursor.Caret;
        int goal = -1;
        // Without Shift the arrows first collapse a selection to its edge, as everywhere.
        if (!extend && cursor.HasSelection && (move == CursorMove.Left || move == CursorMove.Right))
        {
            TextPoint edge = move == CursorMove.Left ? cursor.Start : cursor.End;
            return TextCursor.At(edge);
        }
        string line = LineOf(text, caret.Line);
        int column = Math.Min(caret.Column, line.Length);
        switch (move)
        {
            case CursorMove.Left:
                if (column > 0)
                {
                    caret = new TextPoint(caret.Line, column - 1);
                }
                else if (caret.Line > 0)
                {
                    caret = new TextPoint(caret.Line - 1, LineOf(text, caret.Line - 1).Length);
                }
                break;
            case CursorMove.Right:
                if (column < line.Length)
                {
                    caret = new TextPoint(caret.Line, column + 1);
                }
                else if (caret.Line < text.LineCount - 1)
                {
                    caret = new TextPoint(caret.Line + 1, 0);
                }
                break;
            case CursorMove.Up:
            case CursorMove.Down:
                {
                    goal = cursor.Goal >= 0 ? cursor.Goal : column;
                    int target = caret.Line + (move == CursorMove.Up ? -1 : 1);
                    if (target < 0)
                    {
                        caret = new TextPoint(0, 0);
                    }
                    else if (target >= text.LineCount)
                    {
                        caret = new TextPoint(text.LineCount - 1, LineOf(text, text.LineCount - 1).Length);
                    }
                    else
                    {
                        caret = new TextPoint(target, Math.Min(goal, LineOf(text, target).Length));
                    }
                    break;
                }
            case CursorMove.Home:
                {
                    int first = FirstNonWhitespace(line);
                    caret = new TextPoint(caret.Line, column == first ? 0 : first);
                    break;
                }
            case CursorMove.End:
                caret = new TextPoint(caret.Line, line.Length);
                break;
            case CursorMove.WordLeft:
                caret = WordBoundary(text, new TextPoint(caret.Line, column), forward: false);
                break;
            case CursorMove.WordRight:
                caret = WordBoundary(text, new TextPoint(caret.Line, column), forward: true);
                break;
        }
        return new TextCursor(extend ? cursor.Anchor : caret, caret, goal);
    }

    private static int FirstNonWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }
        return i;
    }

    /// <summary>The place Ctrl+Left or Ctrl+Right goes to: over the whitespace and then over one word, or one run of punctuation.</summary>
    public static TextPoint WordBoundary(IMultiCursorText text, TextPoint from, bool forward)
    {
        string line = LineOf(text, from.Line);
        int i = Math.Min(from.Column, line.Length);
        if (forward)
        {
            if (i >= line.Length)
            {
                return from.Line < text.LineCount - 1 ? new TextPoint(from.Line + 1, 0) : new TextPoint(from.Line, line.Length);
            }
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }
            if (i < line.Length)
            {
                bool word = IsWordChar(line[i]);
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && IsWordChar(line[i]) == word)
                {
                    i++;
                }
            }
            return new TextPoint(from.Line, i);
        }
        if (i == 0)
        {
            return from.Line > 0 ? new TextPoint(from.Line - 1, LineOf(text, from.Line - 1).Length) : from;
        }
        while (i > 0 && char.IsWhiteSpace(line[i - 1]))
        {
            i--;
        }
        if (i > 0)
        {
            bool word = IsWordChar(line[i - 1]);
            while (i > 0 && !char.IsWhiteSpace(line[i - 1]) && IsWordChar(line[i - 1]) == word)
            {
                i--;
            }
        }
        return new TextPoint(from.Line, i);
    }

    // ---------------------------------------------------------------- adding and removing cursors

    /// <summary>Adds a cursor at a place that has none, and takes the one away that already stands there (as long as another is left).</summary>
    public static CursorSet ToggleAt(IMultiCursorText text, CursorSet set, TextPoint point)
    {
        point = Clamp(text, point);
        for (int i = 0; i < set.Count; i++)
        {
            TextCursor cursor = set.Items[i];
            bool hit = cursor.HasSelection ? (point >= cursor.Start && point <= cursor.End) : point == cursor.Caret;
            if (hit && set.Count > 1)
            {
                List<TextCursor> rest = new List<TextCursor>(set.Items);
                rest.RemoveAt(i);
                return CursorSet.Create(rest, Math.Min(set.Primary > i ? set.Primary - 1 : set.Primary, rest.Count - 1));
            }
        }
        if (set.Count >= CursorSet.MaxCursors)
        {
            return set;
        }
        List<TextCursor> all = new List<TextCursor>(set.Items) { TextCursor.At(point) };
        return CursorSet.Create(all, all.Count - 1);
    }

    /// <summary>A cursor on the line above the first one (or below the last one), in the same column.</summary>
    public static CursorSet AddOnAdjacentLine(IMultiCursorText text, CursorSet set, bool below)
    {
        if (set.Count >= CursorSet.MaxCursors)
        {
            return set;
        }
        TextCursor edge = below ? set.Items[set.Count - 1] : set.Items[0];
        int line = edge.Caret.Line + (below ? 1 : -1);
        if (line < 0 || line >= text.LineCount)
        {
            return set;
        }
        int goal = edge.Goal >= 0 ? edge.Goal : edge.Caret.Column;
        TextPoint point = new TextPoint(line, Math.Min(goal, LineOf(text, line).Length));
        List<TextCursor> all = new List<TextCursor>(set.Items) { new TextCursor(point, point, goal) };
        return CursorSet.Create(all, all.Count - 1);
    }

    /// <summary>
    /// Ctrl+D: selects the word under the caret; when something is selected, adds the next place with the same text (after the primary
    /// cursor, from the top of the text when the end is reached).
    /// </summary>
    public static CursorSet AddNextOccurrence(IMultiCursorText text, CursorSet set)
    {
        TextCursor primary = set.PrimaryCursor;
        if (!primary.HasSelection)
        {
            if (!TryGetWordAt(text, primary.Caret, out TextPoint wordStart, out TextPoint wordEnd))
            {
                return set;
            }
            TextCursor[] items = (TextCursor[])set.Items.Clone();
            items[set.Primary] = new TextCursor(wordStart, wordEnd);
            return CursorSet.Create(items, set.Primary, wholeWord: true);
        }
        if (!TryGetNeedle(text, primary, out string needle) || set.Count >= CursorSet.MaxCursors)
        {
            return set;
        }
        int lines = text.LineCount;
        TextPoint from = primary.End;
        // The text after the primary selection first, then the lines from the top, and at last the start of the primary line.
        for (int step = 0; step <= lines; step++)
        {
            int line = (from.Line + step) % lines;
            string lineText = LineOf(text, line);
            int index = step == 0 ? from.Column : 0;
            int end = step == lines ? Math.Min(primary.Start.Column, lineText.Length) : lineText.Length;
            while (index + needle.Length <= end)
            {
                int found = lineText.IndexOf(needle, index, StringComparison.Ordinal);
                if (found < 0 || found + needle.Length > end)
                {
                    break;
                }
                TextPoint foundStart = new TextPoint(line, found);
                TextPoint foundEnd = new TextPoint(line, found + needle.Length);
                if ((!set.WholeWord || IsWholeWord(lineText, found, needle.Length)) && !Overlaps(set, foundStart, foundEnd))
                {
                    List<TextCursor> all = new List<TextCursor>(set.Items) { new TextCursor(foundStart, foundEnd) };
                    return CursorSet.Create(all, all.Count - 1, set.WholeWord);
                }
                index = found + 1;
            }
        }
        return set;
    }

    /// <summary>Ctrl+Shift+L: a cursor on every place that has the text of the primary selection (or the word under the caret).</summary>
    public static CursorSet SelectAllOccurrences(IMultiCursorText text, CursorSet set)
    {
        TextCursor primary = set.PrimaryCursor;
        bool wholeWord = set.WholeWord;
        if (!primary.HasSelection)
        {
            if (!TryGetWordAt(text, primary.Caret, out TextPoint wordStart, out TextPoint wordEnd))
            {
                return set;
            }
            primary = new TextCursor(wordStart, wordEnd);
            wholeWord = true;
        }
        if (!TryGetNeedle(text, primary, out string needle))
        {
            return set;
        }
        List<TextCursor> found = new List<TextCursor>();
        int primaryIndex = 0;
        for (int line = 0; line < text.LineCount && found.Count < CursorSet.MaxCursors; line++)
        {
            string lineText = LineOf(text, line);
            int index = 0;
            while (index <= lineText.Length - needle.Length && found.Count < CursorSet.MaxCursors)
            {
                int at = lineText.IndexOf(needle, index, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }
                if (!wholeWord || IsWholeWord(lineText, at, needle.Length))
                {
                    if (new TextPoint(line, at) == primary.Start)
                    {
                        primaryIndex = found.Count;
                    }
                    found.Add(new TextCursor(new TextPoint(line, at), new TextPoint(line, at + needle.Length)));
                    index = at + needle.Length;
                }
                else
                {
                    index = at + 1;
                }
            }
        }
        return found.Count == 0 ? set : CursorSet.Create(found, primaryIndex, wholeWord);
    }

    /// <summary>Alt+Shift+click: a cursor on every line from the anchor to the clicked place, in the same columns (a rectangle of text).</summary>
    public static CursorSet SelectColumns(IMultiCursorText text, TextPoint anchor, TextPoint caret)
    {
        anchor = Clamp(text, anchor);
        caret = Clamp(text, caret);
        int first = Math.Min(anchor.Line, caret.Line);
        int last = Math.Max(anchor.Line, caret.Line);
        if (last - first + 1 > CursorSet.MaxCursors)
        {
            last = first + CursorSet.MaxCursors - 1;
        }
        List<TextCursor> cursors = new List<TextCursor>();
        for (int line = first; line <= last; line++)
        {
            int length = LineOf(text, line).Length;
            cursors.Add(new TextCursor(new TextPoint(line, Math.Min(anchor.Column, length)), new TextPoint(line, Math.Min(caret.Column, length)), caret.Column));
        }
        // The cursor on the clicked line is the primary one.
        return CursorSet.Create(cursors, caret.Line >= first && caret.Line <= last ? caret.Line - first : 0);
    }

    private static bool TryGetWordAt(IMultiCursorText text, TextPoint point, out TextPoint start, out TextPoint end)
    {
        string line = LineOf(text, point.Line);
        int column = Math.Min(point.Column, line.Length);
        int from = column;
        int to = column;
        while (from > 0 && IsWordChar(line[from - 1]))
        {
            from--;
        }
        while (to < line.Length && IsWordChar(line[to]))
        {
            to++;
        }
        start = new TextPoint(point.Line, from);
        end = new TextPoint(point.Line, to);
        return to > from;
    }

    private static bool TryGetNeedle(IMultiCursorText text, TextCursor cursor, out string needle)
    {
        needle = null;
        if (cursor.Start.Line != cursor.End.Line)
        {
            return false;
        }
        string line = LineOf(text, cursor.Start.Line);
        if (cursor.End.Column > line.Length)
        {
            return false;
        }
        needle = line.Substring(cursor.Start.Column, cursor.End.Column - cursor.Start.Column);
        return needle.Length > 0;
    }

    private static bool IsWholeWord(string line, int index, int length)
    {
        return (index == 0 || !IsWordChar(line[index - 1])) && (index + length >= line.Length || !IsWordChar(line[index + length]));
    }

    private static bool Overlaps(CursorSet set, TextPoint start, TextPoint end)
    {
        foreach (TextCursor cursor in set.Items)
        {
            if (cursor.Start < end && start < cursor.End)
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>The selected text of every cursor that has a selection, in the order of the text; empty when none has.</summary>
    public static List<string> SelectedTexts(IMultiCursorText text, CursorSet set)
    {
        List<string> result = new List<string>();
        foreach (TextCursor cursor in set.Items)
        {
            if (cursor.HasSelection)
            {
                result.Add(GetRange(text, cursor.Start, cursor.End));
            }
        }
        return result;
    }

    public static string GetRange(IMultiCursorText text, TextPoint start, TextPoint end)
    {
        if (start.Line == end.Line)
        {
            string line = LineOf(text, start.Line);
            int from = Math.Min(start.Column, line.Length);
            return line.Substring(from, Math.Max(0, Math.Min(end.Column, line.Length) - from));
        }
        StringBuilder builder = new StringBuilder();
        string first = LineOf(text, start.Line);
        builder.Append(first.Substring(Math.Min(start.Column, first.Length)));
        for (int line = start.Line + 1; line < end.Line; line++)
        {
            builder.Append('\n').Append(LineOf(text, line));
        }
        string last = LineOf(text, end.Line);
        builder.Append('\n').Append(last.Substring(0, Math.Min(end.Column, last.Length)));
        return builder.ToString();
    }

    /// <summary>
    /// What Paste gives each cursor: when the clipboard has as many lines as there are cursors, every cursor gets one line (the
    /// lines that were copied from several cursors go back to several cursors); otherwise null, and every cursor gets all of the text.
    /// </summary>
    public static string[] SplitForCursors(string clipboard, int cursors)
    {
        if (cursors < 2 || string.IsNullOrEmpty(clipboard))
        {
            return null;
        }
        string trimmed = clipboard;
        // A line break at the very end (a copied line) does not start a line of its own.
        if (trimmed.EndsWith("\r\n", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 2);
        }
        else if (trimmed.EndsWith("\n", StringComparison.Ordinal) || trimmed.EndsWith("\r", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }
        string[] lines = SplitLines(trimmed);
        return lines.Length == cursors ? lines : null;
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Puts the same text at every cursor, in place of its selection.</summary>
    public static EditPlan Insert(IMultiCursorText text, CursorSet set, string insertion)
    {
        string[] texts = new string[set.Count];
        for (int i = 0; i < texts.Length; i++)
        {
            texts[i] = insertion;
        }
        return InsertPerCursor(text, set, texts);
    }

    /// <summary>Puts a text of its own at every cursor, in place of its selection.</summary>
    public static EditPlan InsertPerCursor(IMultiCursorText text, CursorSet set, string[] insertions)
    {
        if (insertions.Length != set.Count)
        {
            throw new ArgumentException("One text for each cursor is needed.", nameof(insertions));
        }
        EditPlan plan = new EditPlan();
        for (int i = 0; i < set.Count; i++)
        {
            TextCursor cursor = set.Items[i];
            plan.Edits.Add(new TextEdit(cursor.Start, cursor.End, insertions[i]));
        }
        Finish(text, plan, set);
        return plan;
    }

    /// <summary>Enter: a line break and the indentation of the line, as far as it is in front of the cursor.</summary>
    public static EditPlan NewLine(IMultiCursorText text, CursorSet set, string lineBreak)
    {
        string[] texts = new string[set.Count];
        for (int i = 0; i < texts.Length; i++)
        {
            TextCursor cursor = set.Items[i];
            string line = LineOf(text, cursor.Start.Line);
            int indent = Math.Min(FirstNonWhitespace(line), cursor.Start.Column);
            texts[i] = lineBreak + line.Substring(0, indent);
        }
        return InsertPerCursor(text, set, texts);
    }

    /// <summary>Tab: to the next tab stop (spaces) or a tab character, in place of the selection.</summary>
    public static EditPlan Tab(IMultiCursorText text, CursorSet set, int tabSize, bool insertSpaces)
    {
        tabSize = Math.Max(1, tabSize);
        string[] texts = new string[set.Count];
        for (int i = 0; i < texts.Length; i++)
        {
            if (!insertSpaces)
            {
                texts[i] = "\t";
                continue;
            }
            TextCursor cursor = set.Items[i];
            int column = DisplayColumn(LineOf(text, cursor.Start.Line), cursor.Start.Column, tabSize);
            texts[i] = new string(' ', tabSize - column % tabSize);
        }
        return InsertPerCursor(text, set, texts);
    }

    public static int DisplayColumn(string line, int index, int tabSize)
    {
        int column = 0;
        for (int i = 0; i < index && i < line.Length; i++)
        {
            column = line[i] == '\t' ? column + tabSize - column % tabSize : column + 1;
        }
        return column;
    }

    /// <summary>
    /// Backspace: the selection, or the character in front of the caret, or the line break when the caret is at the start of a line. With
    /// <paramref name="spaceIndent"/> (the width of a tab; 0 to switch it off) a caret inside leading spaces goes back to the previous tab stop.
    /// </summary>
    public static EditPlan Backspace(IMultiCursorText text, CursorSet set, int spaceIndent = 0)
    {
        EditPlan plan = new EditPlan();
        foreach (TextCursor cursor in set.Items)
        {
            if (cursor.HasSelection)
            {
                plan.Edits.Add(new TextEdit(cursor.Start, cursor.End, ""));
                continue;
            }
            TextPoint caret = cursor.Caret;
            string line = LineOf(text, caret.Line);
            int column = Math.Min(caret.Column, line.Length);
            if (column > 0)
            {
                int length = 1;
                if (spaceIndent > 0 && IsAllSpaces(line, column))
                {
                    length = (column - 1) % spaceIndent + 1;
                }
                else if (column > 1 && char.IsLowSurrogate(line[column - 1]) && char.IsHighSurrogate(line[column - 2]))
                {
                    length = 2;
                }
                plan.Edits.Add(new TextEdit(new TextPoint(caret.Line, column - length), new TextPoint(caret.Line, column), ""));
            }
            else if (caret.Line > 0)
            {
                plan.Edits.Add(new TextEdit(new TextPoint(caret.Line - 1, LineOf(text, caret.Line - 1).Length), new TextPoint(caret.Line, 0), ""));
            }
            else
            {
                plan.Edits.Add(new TextEdit(caret, caret, ""));
            }
        }
        Finish(text, plan, set);
        return plan;
    }

    private static bool IsAllSpaces(string line, int upTo)
    {
        for (int i = 0; i < upTo; i++)
        {
            if (line[i] != ' ')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Delete: the selection, or the character after the caret, or the line break at the end of a line.</summary>
    public static EditPlan Delete(IMultiCursorText text, CursorSet set)
    {
        EditPlan plan = new EditPlan();
        foreach (TextCursor cursor in set.Items)
        {
            if (cursor.HasSelection)
            {
                plan.Edits.Add(new TextEdit(cursor.Start, cursor.End, ""));
                continue;
            }
            TextPoint caret = cursor.Caret;
            string line = LineOf(text, caret.Line);
            int column = Math.Min(caret.Column, line.Length);
            if (column < line.Length)
            {
                int length = column + 1 < line.Length && char.IsHighSurrogate(line[column]) && char.IsLowSurrogate(line[column + 1]) ? 2 : 1;
                plan.Edits.Add(new TextEdit(new TextPoint(caret.Line, column), new TextPoint(caret.Line, column + length), ""));
            }
            else if (caret.Line < text.LineCount - 1)
            {
                plan.Edits.Add(new TextEdit(new TextPoint(caret.Line, line.Length), new TextPoint(caret.Line + 1, 0), ""));
            }
            else
            {
                plan.Edits.Add(new TextEdit(caret, caret, ""));
            }
        }
        Finish(text, plan, set);
        return plan;
    }

    /// <summary>Ctrl+Backspace and Ctrl+Delete: the selection, or the word behind or in front of the caret.</summary>
    public static EditPlan DeleteWord(IMultiCursorText text, CursorSet set, bool forward)
    {
        EditPlan plan = new EditPlan();
        foreach (TextCursor cursor in set.Items)
        {
            if (cursor.HasSelection)
            {
                plan.Edits.Add(new TextEdit(cursor.Start, cursor.End, ""));
                continue;
            }
            TextPoint caret = Clamp(text, cursor.Caret);
            TextPoint other = WordBoundary(text, caret, forward);
            plan.Edits.Add(forward ? new TextEdit(caret, other, "") : new TextEdit(other, caret, ""));
        }
        Finish(text, plan, set);
        return plan;
    }

    /// <summary>Cut: removes the selections (the cursors that have none stay as they are).</summary>
    public static EditPlan DeleteSelections(IMultiCursorText text, CursorSet set)
    {
        EditPlan plan = new EditPlan();
        foreach (TextCursor cursor in set.Items)
        {
            plan.Edits.Add(new TextEdit(cursor.Start, cursor.End, ""));
        }
        Finish(text, plan, set);
        return plan;
    }

    // ---------------------------------------------------------------- applying

    // One edit for each cursor, in the order of the cursors. The edits of cursors that reach into each other (two Ctrl+Backspace
    // in one word) are made one, and then it is worked out where each cursor stands after all of the edits.
    private static void Finish(IMultiCursorText text, EditPlan plan, CursorSet set)
    {
        // The editor takes the indentation of the next line with it when a deletion ends at the start of that line and begins at the
        // end of the one above (joining two lines): the plan has to say the same, or the cursors would stand in the wrong columns.
        foreach (TextEdit joined in plan.Edits)
        {
            if (joined.Text.Length == 0 && joined.End.Column == 0 && joined.End.Line == joined.Start.Line + 1
                && joined.Start.Column == LineOf(text, joined.Start.Line).Length)
            {
                string above = LineOf(text, joined.Start.Line);
                string below = LineOf(text, joined.End.Line);
                int common = 0;
                int indentation = FirstNonWhitespace(above);
                while (common < indentation && common < below.Length && above[common] == below[common])
                {
                    common++;
                }
                joined.End = new TextPoint(joined.End.Line, common);
            }
        }
        int[] order = new int[plan.Edits.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) =>
        {
            int result = plan.Edits[a].Start.CompareTo(plan.Edits[b].Start);
            if (result == 0)
            {
                result = plan.Edits[a].End.CompareTo(plan.Edits[b].End);
            }
            return result != 0 ? result : a.CompareTo(b);
        });
        List<TextEdit> merged = new List<TextEdit>(order.Length);
        int[] owner = new int[order.Length];
        foreach (int index in order)
        {
            TextEdit edit = plan.Edits[index];
            if (merged.Count > 0 && edit.Start < merged[merged.Count - 1].End)
            {
                TextEdit last = merged[merged.Count - 1];
                if (edit.End > last.End)
                {
                    last.End = edit.End;
                }
                last.Text += edit.Text;
            }
            else
            {
                merged.Add(new TextEdit(edit.Start, edit.End, edit.Text));
            }
            owner[index] = merged.Count - 1;
        }
        plan.Edits = merged;

        TextCursor[] after = new TextCursor[merged.Count];
        int lineShift = 0;
        // The previous edit's end, in the old text, and how far the columns behind it on its line have moved.
        int previousEndLine = -1;
        int columnShift = 0;
        for (int i = 0; i < merged.Count; i++)
        {
            TextEdit edit = merged[i];
            int startColumn = edit.Start.Column + (edit.Start.Line == previousEndLine ? columnShift : 0);
            TextPoint newStart = new TextPoint(edit.Start.Line + lineShift, startColumn);
            TextPoint newEnd = EndOfInsertion(newStart, edit.Text);
            after[i] = TextCursor.At(newEnd);
            lineShift = newEnd.Line - edit.End.Line;
            columnShift = newEnd.Column - edit.End.Column;
            previousEndLine = edit.End.Line;
        }
        plan.Result = CursorSet.Create(after, owner[set.Primary]);
    }

    /// <summary>
    /// Applies the edits to a list of lines (the reference for the editor, which makes the same edits in its own document, last first).
    /// </summary>
    public static List<string> Apply(IList<string> lines, EditPlan plan)
    {
        List<string> result = new List<string>(lines);
        for (int i = plan.Edits.Count - 1; i >= 0; i--)
        {
            TextEdit edit = plan.Edits[i];
            string head = result[edit.Start.Line].Substring(0, Math.Min(edit.Start.Column, result[edit.Start.Line].Length));
            string endLine = result[edit.End.Line];
            string tail = endLine.Substring(Math.Min(edit.End.Column, endLine.Length));
            string[] inserted = SplitLines(edit.Text);
            result.RemoveRange(edit.Start.Line, edit.End.Line - edit.Start.Line + 1);
            List<string> middle = new List<string>(inserted);
            middle[0] = head + middle[0];
            middle[middle.Count - 1] += tail;
            result.InsertRange(edit.Start.Line, middle);
        }
        return result;
    }
}
