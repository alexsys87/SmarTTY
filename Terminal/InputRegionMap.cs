using SmarTTY.TextEditing;
using System.Text;

namespace SmarTTY.Terminal;

/// <summary>
/// The text of a command line that the user edits in place in the document of a terminal, as a region of that document: it starts at a column of a
/// line (after the prompt) and runs to the end of the document, over as many rows as the line wraps over. The indices count the characters of the
/// region as one text, rows after one another; a row end and the start of the next row are the same index (the caret at the end of a row and at the
/// start of the next one is at the same place of the text).
/// </summary>
public static class InputRegionMap
{
    /// <summary>The text of the region: every line from <paramref name="startLine"/> to the end of the document, from <paramref name="startColumn"/> of the first.</summary>
    public static string GetText(EditableTextDocument document, int startLine, int startColumn)
    {
        StringBuilder stringBuilder = new StringBuilder();
        EditableTextDocument editableTextDocument = document;
        for (int editableTextDocumentIndex = startLine; editableTextDocumentIndex < editableTextDocument.LineCount; editableTextDocumentIndex++)
        {
            string value = editableTextDocument.GetLineText(editableTextDocumentIndex);
            stringBuilder.Append(value);
        }
        return stringBuilder.ToString().Substring(startColumn);
    }

    /// <summary>The index in the text of the region of a position of the document (-1 when the position is above the region).</summary>
    public static int IndexOf(EditableTextDocument document, int startLine, int startColumn, TextDocument.TextPosition textPosition)
    {
        EditableTextDocument editableTextDocument = document;
        int offset = -startColumn;
        for (int line = startLine; line < editableTextDocument.LineCount; line++)
        {
            int length = editableTextDocument.GetLineText(line).Length;
            if (line == textPosition.Line)
            {
                return offset + textPosition.Column;
            }
            offset += length;
        }
        return -1;
    }

    /// <summary>The position in the document of an index in the text of the region (the end of the document for an index that is negative or past the text).</summary>
    public static TextDocument.TextPosition PositionAt(EditableTextDocument document, int startLine, int startColumn, int index)
    {
        if (index >= 0)
        {
            int targetColumn = index + startColumn;
            int charsBefore = 0;
            for (int line = startLine; line < document.LineCount; line++)
            {
                int length = document.GetLineText(line).Length;
                if (targetColumn >= charsBefore && targetColumn <= charsBefore + length)
                {
                    return new TextDocument.TextPosition(targetColumn - charsBefore, line);
                }
                charsBefore += length;
            }
        }
        return document.EndPosition;
    }
}
