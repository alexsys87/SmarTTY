using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Linq;

// Checks of the model of the hex view of a device terminal (TextEditing/HexTerminalModel.cs): the rows the bytes are formatted into, the chunks and the breaks, the
// layout that is made again, the match that the search selected and where the search goes on from, and where a byte is in the two views.
internal static class Program
{
    private static readonly DateTime Start = new DateTime(2026, 1, 1, 12, 0, 0);

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static byte[] Bytes(int count, int first = 0)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(first + i);
        }
        return bytes;
    }

    private static string Rows(List<HexRow> rows)
    {
        return string.Join("|", rows.Select((HexRow row) => row.Hex + "/" + row.Text + "/" + row.ByteCount + (row.EndsRow ? "+" : "")));
    }

    private static TextDocument.TextPosition Position(int column, int line)
    {
        return new TextDocument.TextPosition(column, line);
    }

    private static void FormatChecks()
    {
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(5, 0x41), 5, 0, 4)), "41 42 43 44/ABCD/4+|45/E/1", "bytes are formatted into rows of the width, the last one is not full");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(4, 0x41), 4, 0, 4)), "41 42 43 44/ABCD/4", "bytes that fill a row exactly leave it as the last row, not ended");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(0), 0, 0, 4)), "//0", "no bytes make one empty row");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(2, 0x41), 2, 3, 4)), " 41/A/1+|42/B/1", "bytes that go on in a row that has 3 bytes: the first goes after a blank and fills it, the next starts a new one");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(1, 0x41), 1, 4, 4)), "//0+|41/A/1", "a first byte at a full row ends it (empty) first");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(1, 0x41), 1, 9, 4)), "//0+|41/A/1", "a row that is fuller than the width counts as full");
        Equal(Rows(HexTerminalModel.FormatRows(new byte[] { 0x00, 0x1f, 0x20, 0x7e, 0x7f, 0x80, 0xff, 0x0a }, 8, 0, 8)), "00 1f 20 7e 7f 80 ff 0a/.. ~..../8", "only the printable ASCII is shown as it is");
        Equal(Rows(HexTerminalModel.FormatRows(new byte[] { 0xab, 0x0f }, 2, 0, 16)), "ab 0f/../2", "digits are lowercase with two digits each");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(3, 1), 3, 0, 1)), "01/./1+|02/./1+|03/./1", "one byte in a row");
        Equal(Rows(HexTerminalModel.FormatRows(Bytes(10, 1), 2, 0, 4)), "01 02/../2", "only the bytes asked for are taken");
    }

    private static void BreakChecks()
    {
        HexTerminalModel model = new HexTerminalModel();
        Check(!model.ShouldBreak(false, true, 10, Start), "no break before the first data");
        model.AddData(Bytes(3), 3, false, Start);
        Check(!model.ShouldBreak(false, true, 10, Start.AddMilliseconds(9)), "no break when the data comes soon after");
        Check(model.ShouldBreak(false, true, 10, Start.AddMilliseconds(10)), "a break when the pause is as long as the timeout");
        Check(!model.ShouldBreak(false, true, 0, Start.AddDays(1)), "a timeout of 0 never breaks for a pause");
        Check(!model.ShouldBreak(false, true, -5, Start.AddDays(1)), "nor does a negative one");
        Check(model.ShouldBreak(true, true, 0, Start), "a break around the echo when it is asked for");
        Check(!model.ShouldBreak(true, false, 0, Start), "and none when it is not");
        Check(!model.ShouldBreak(false, true, 0, Start), "no break when the kind of data is the same");
        model.AddData(Bytes(1), 1, true, Start);
        Check(model.ShouldBreak(false, true, 0, Start), "the data after the echo is a new message");
        model.Clear();
        Check(!model.ShouldBreak(true, true, 1, Start.AddDays(1)), "Clear forgets when the data came");
    }

    private static void DataChecks()
    {
        HexTerminalModel model = new HexTerminalModel();
        Equal(model.AddData(Bytes(5), 99, false, Start), 5, "a count past the buffer is the length of the buffer");
        Equal(model.AddData(Bytes(5), -4, false, Start), 0, "a negative count is 0");
        Equal(model.Stream.Length, 5, "only the bytes taken are in the stream");
        Equal(model.Chunks.Count, 1, "data of the same kind goes on in the chunk");
        Check(model.Chunks[0].Start == 0 && model.Chunks[0].Count == 5 && !model.Chunks[0].IsEcho && !model.Chunks[0].IsBreak, "which holds all of it");
        model.AddData(Bytes(2), 2, true, Start);
        model.AddData(Bytes(3), 3, true, Start);
        Equal(model.Chunks.Count, 2, "an echo is a chunk of its own, and goes on");
        Check(model.Chunks[1].Start == 5 && model.Chunks[1].Count == 5 && model.Chunks[1].IsEcho, "from where the stream was");
        model.AddBreak();
        Check(model.Chunks[2].IsBreak && model.Chunks[2].Start == 10 && model.Chunks[2].Count == 0, "a break is a chunk at the end of the stream");
        model.AddData(Bytes(2), 2, true, Start);
        Equal(model.Chunks.Count, 4, "data after a break is a new chunk, even of the same kind");
        Check(model.Chunks[3].Start == 10 && model.Chunks[3].Count == 2 && model.Chunks[3].IsEcho, "that starts where the break was");

        // Data of the kind that the chunk before the break had does not go on in the break.
        HexTerminalModel afterBreak = new HexTerminalModel();
        afterBreak.AddData(Bytes(2), 2, false, Start);
        afterBreak.AddBreak();
        afterBreak.AddData(Bytes(2), 2, false, Start);
        Check(afterBreak.Chunks.Count == 3 && afterBreak.Chunks[1].IsBreak && afterBreak.Chunks[1].Count == 0 && afterBreak.Chunks[2].Count == 2 && afterBreak.Chunks[2].Start == 2, "data after a break is a chunk of its own, and the break stays empty");

        // The break is in the stream too: a match does not go across it.
        HexTerminalModel other = new HexTerminalModel();
        other.AddData(new byte[] { 1, 2 }, 2, false, Start);
        other.AddBreak();
        other.AddData(new byte[] { 3, 4 }, 2, false, Start);
        Equal(other.Stream.Find(new byte[] { 2, 3 }, 0, false, true), -1, "a sequence does not go across a break");
        Equal(other.Stream.Find(new byte[] { 3, 4 }, 0, false, true), 2, "but is found after it");
        model.Clear();
        Check(model.Chunks.Count == 0 && model.Stream.Length == 0 && model.MatchOffset == -1, "Clear forgets chunks, stream and match");
    }

    private static void LayoutChecks()
    {
        HexTerminalModel model = new HexTerminalModel();
        Equal(model.BytesPerLine, 16, "16 bytes in a row to begin with");
        Check(!model.NeedsRelayout, "no layout to make again without data");
        model.BytesPerLine = 8;
        model.BeginLayout();
        Check(model.LaidOutBytesPerLine == 8 && !model.LayoutMixed, "the first data is laid out as it comes");
        Check(!model.NeedsRelayout, "and without chunks there is nothing to lay out");
        model.AddData(Bytes(4), 4, false, Start);
        Check(!model.NeedsRelayout, "data laid out with the number of bytes that is set needs nothing");
        model.BytesPerLine = 4;
        Check(model.NeedsRelayout, "another number of bytes in a row needs a new layout");
        model.BeginLayout();
        Check(model.LayoutMixed && model.LaidOutBytesPerLine == 4, "data laid out with another number makes the layout mixed");
        Check(model.NeedsRelayout, "which needs a new layout even if the number is the same again");
        model.Stream.MapBytes(0, 4);
        Equal(model.Stream.RowCount, 1, "rows of the stream");
        model.SetMatch(2, 1, Position(1, 1), Position(2, 2));
        model.BeginRelayout();
        Check(!model.LayoutMixed && model.LaidOutBytesPerLine == 0 && model.MatchOffset == -1 && model.Stream.RowCount == 0 && model.Stream.Length == 4, "a new layout forgets rows, match and mixing, and keeps the bytes");
        Check(model.NeedsRelayout, "and nothing is laid out yet");
        model.BeginLayout();
        Check(!model.NeedsRelayout && !model.LayoutMixed, "after it the layout is the one that is set");
        model.Clear();
        Check(model.LaidOutBytesPerLine == 0 && !model.LayoutMixed, "Clear forgets the layout");
    }

    private static void MatchChecks()
    {
        HexTerminalModel model = new HexTerminalModel();
        model.AddData(Bytes(8), 8, false, Start);
        model.Stream.MapBytes(0, 4);
        model.Stream.MapBytes(1, 4);
        Check(!model.IsMatchSelected(true, Position(0, 0), Position(2, 0)), "no match is selected before one was found");
        model.SetMatch(2, 3, Position(6, 0), Position(2, 1));
        Equal(model.MatchOffset + "+" + model.MatchLength, "2+3", "the match found last");
        Check(model.IsMatchSelected(true, Position(6, 0), Position(2, 1)), "it is selected while the HEX view has its selection");
        Check(!model.IsMatchSelected(false, Position(6, 0), Position(2, 1)), "not when nothing is selected");
        Check(!model.IsMatchSelected(true, Position(7, 0), Position(2, 1)), "nor when the start of the selection moved");
        Check(!model.IsMatchSelected(true, Position(6, 0), Position(3, 1)), "nor when its end moved");
        Equal(model.GetSearchStart(false, true, Position(0, 0), Position(0, 0)), 3, "the search goes on one byte behind the match");
        Equal(model.GetSearchStart(true, true, Position(0, 0), Position(0, 0)), 1, "and backward one byte before it");
        Equal(model.GetSearchStart(false, false, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid), 0, "without a selection it starts at the beginning");
        Equal(model.GetSearchStart(true, false, TextDocument.TextPosition.Invalid, TextDocument.TextPosition.Invalid), -1, "backward from the beginning is before it");
        Equal(model.GetSearchStart(false, false, Position(3, 1), Position(3, 1)), 5, "from a caret in the second row forward: the first byte at or after it");
        Equal(model.GetSearchStart(true, false, Position(3, 1), Position(3, 1)), 4, "backward: the byte before that");
        Equal(model.GetSearchStart(false, false, Position(0, 0), Position(3, 1)), 5, "of the two ends of a selection the one in the direction of the search counts (forward: the later)");
        Equal(model.GetSearchStart(true, false, Position(3, 1), Position(0, 0)), 0 - 1, "backward: the earlier");
        Equal(model.GetSearchStart(false, false, Position(3, 1), Position(0, 0)), 5, "forward with the end before the start: the start");
        Equal(model.GetSearchStart(true, false, Position(3, 1), TextDocument.TextPosition.Invalid), 4, "backward with no end of the selection: the start");
        Equal(model.GetSearchStart(false, false, Position(3, 1), TextDocument.TextPosition.Invalid), 5, "forward with no end of the selection: the start");
        model.ClearMatch();
        Equal(model.MatchOffset, -1, "the match is forgotten");
        Check(!model.IsMatchSelected(true, Position(6, 0), Position(2, 1)), "and is not selected any more");
    }

    private static void ColumnChecks()
    {
        Equal(HexTerminalModel.HexStartColumn(0), 0, "the first digit of the first byte");
        Equal(HexTerminalModel.HexStartColumn(4), 12, "three columns per byte");
        Equal(HexTerminalModel.HexEndColumn(0), 2, "the end of a byte is after its second digit");
        Equal(HexTerminalModel.HexEndColumn(4), 14, "and the blank after it is not counted");

        // HEX selection to the text view: a selection from left to right takes whole bytes (the start rounds up, the end too);
        // one from right to left takes the same bytes.
        TextDocument.TextPosition start = Position(3, 1);
        TextDocument.TextPosition end = Position(8, 2);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "1,3", "left to right");
        Equal(start.Line + "," + end.Line, "1,2", "the rows stay");
        start = Position(8, 2);
        end = Position(3, 1);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "3,1", "right to left");
        start = Position(4, 0);
        end = Position(4, 0);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "1,2", "an empty selection in the middle of a byte takes the byte");
        start = Position(4, 1);
        end = Position(10, 1);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "1,4", "left to right with columns that are not a multiple of three: the start rounds down to the byte it is in, the end up");
        start = Position(10, 1);
        end = Position(4, 1);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "4,1", "right to left with such columns: the other way round");
        start = Position(7, 1);
        end = Position(2, 1);
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Equal(start.Column + "," + end.Column, "3,1", "right to left again");

        // And the other way: a selection of the text view in the HEX view.
        start = Position(1, 1);
        end = Position(3, 2);
        HexTerminalModel.TextSelectionToHex(ref start, ref end);
        Equal(start.Column + "," + end.Column, "3,8", "left to right: from the first digit of the first byte to the last digit of the last");
        start = Position(3, 2);
        end = Position(1, 1);
        HexTerminalModel.TextSelectionToHex(ref start, ref end);
        Equal(start.Column + "," + end.Column, "8,3", "right to left");
        start = Position(0, 0);
        end = Position(0, 0);
        HexTerminalModel.TextSelectionToHex(ref start, ref end);
        Equal(start.Column + "," + end.Column, "0,0", "an empty selection at the start does not go below column 0");
        start = Position(0, 2);
        end = Position(0, 1);
        HexTerminalModel.TextSelectionToHex(ref start, ref end);
        Equal(start.Column + "," + end.Column, "0,0", "right to left from column 0 does not go below it either");
        start = Position(0, 1);
        end = Position(0, 2);
        HexTerminalModel.TextSelectionToHex(ref start, ref end);
        Equal(start.Column + "," + end.Column, "0,0", "nor left to right to column 0");
    }

    private static int Main()
    {
        try
        {
            FormatChecks();
            BreakChecks();
            DataChecks();
            LayoutChecks();
            MatchChecks();
            ColumnChecks();
            HexPaneChecks();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        return 0;
    }

    // The two panes of the HEX view of a device (GUI/TerminalView/WpfHexTerminal) are display models written row by row, as the view does it.
    private static void HexPaneChecks()
    {
        TerminalDisplayModel hex = new TerminalDisplayModel(12, 4);
        TerminalDisplayModel text = new TerminalDisplayModel(4, 4);
        hex.AutoWrap = false;
        text.AutoWrap = false;
        HexTerminalModel model = new HexTerminalModel { BytesPerLine = 4 };
        byte[] data = { 0x41, 0x42, 0x0a, 0x43, 0x44, 0x7f };
        int count = model.AddData(data, data.Length, false, DateTime.Now);
        model.BeginLayout();
        foreach (HexRow row in HexTerminalModel.FormatRows(data, count, (hex.CursorPosition.X + 1) / 3, model.BytesPerLine))
        {
            model.Stream.MapBytes(hex.CursorDocumentLine, row.ByteCount);
            hex.WriteText(row.Hex);
            text.WriteText(row.Text);
            if (row.EndsRow)
            {
                hex.MoveCursorBy(-hex.CursorPosition.X, 1, scroll: true);
                text.MoveCursorBy(-text.CursorPosition.X, 1, scroll: true);
            }
        }
        Equal(hex.Document.GetLineText(0).TrimEnd() + "|" + hex.Document.GetLineText(1).TrimEnd(), "41 42 0a 43|44 7f", "the hex rows, four bytes in a row");
        Equal(text.Document.GetLineText(0) + "|" + text.Document.GetLineText(1).TrimEnd(), "AB.C|D.", "the characters, a dot for a byte that is not shown");
        Check(model.Stream.TryLocate(4, out int line, out int index) && line == 1 && index == 0, "the stream knows the row of a byte");
        hex.CurrentStyle = new CellStyle { Override = new CellStyle.ForegroundOverride { Color = new RgbColor(0xFF00FF00) } };
        hex.WriteText("00");
        hex.CurrentStyle = CellStyle.Default;
        Check(hex.Lines.GetLine(1).Cells[5].Override.Color.HasValue, "the echo is written in a colour of its own");
        int selections = 0;
        hex.SelectionChanged += () => selections++;
        hex.Select(new TextDocument.TextPosition(5, 0), new TextDocument.TextPosition(0, 0));
        Equal(hex.SelectionAnchor.Column + ":" + hex.SelectionEnd.Column, "5:0", "the anchor and the end of a selection made backward");
        TextDocument.TextPosition start = hex.SelectionAnchor;
        TextDocument.TextPosition end = hex.SelectionEnd;
        HexTerminalModel.HexSelectionToText(ref start, ref end);
        Check(start.Line == 0 && end.Line == 0, "the selection of the hex pane is mirrored in the row of the text pane");
        hex.ClearSelection();
        hex.ClearSelection();
        Equal(selections, 2, "a selection and its removal are told, a removal of nothing is not");
    }
}

// The namespaces of the code the logic folders use: an empty one would make "using" fail before the real missing file is named.
namespace SmarTTY.IO
{
    internal static class NamespaceSeed
    {
    }
}

namespace SmarTTY.Ssh
{
    internal static class NamespaceSeed
    {
    }
}
