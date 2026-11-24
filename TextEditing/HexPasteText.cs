using System;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>The answer to the question whether an ambiguous clipboard text is bytes or text.</summary>
public enum HexPasteChoice
{
    /// <summary>Paste it as hexadecimal bytes.</summary>
    Bytes,

    /// <summary>Paste it as text (its UTF-8 bytes).</summary>
    Text,

    /// <summary>Do not paste.</summary>
    Cancel
}

/// <summary>
/// The bytes a paste into the hex editor inserts: in the character column the UTF-8 of the text; in the hex column the bytes of a text that is
/// hexadecimal, the UTF-8 of one that is not, and for a text that can be both ("dead") what the user answers to a question. It is the part of
/// <c>HexEditorControl.Paste</c> between reading the clipboard and inserting the bytes, moved here as it was; the view reads the clipboard, asks the
/// question with its dialog and inserts the bytes.
/// </summary>
public static class HexPasteText
{
    /// <summary>
    /// The bytes of <paramref name="text"/> pasted into <paramref name="area"/>, or null when the user cancelled. <paramref name="ask"/> shows the
    /// question it is given and returns the answer.
    /// </summary>
    public static byte[] ToBytes(string text, HexEditArea area, Func<string, HexPasteChoice> ask)
    {
        if (area == HexEditArea.Ascii)
        {
            return Encoding.UTF8.GetBytes(text);
        }
        switch (HexDocument.ClassifyClipboard(text))
        {
            case HexDocument.ClipboardKind.Hex:
                return HexDocument.ParseClipboardBytes(text, preferHex: true);
            case HexDocument.ClipboardKind.Ambiguous:
                // "dead" is DE AD as hex and 64 65 61 64 as text: only the user knows which was copied.
                byte[] asHex = HexDocument.ParseClipboardBytes(text, preferHex: true);
                byte[] asText = Encoding.UTF8.GetBytes(text);
                byte[] textStart = new byte[Math.Min(asText.Length, 8)];
                Array.Copy(asText, textStart, textStart.Length);
                HexPasteChoice answer = ask(
                    "The clipboard holds \"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text.Trim()) + "\", which can be hexadecimal bytes or text.\n\n"
                    + "Yes: paste as bytes (" + HexDocument.FormatHex(asHex) + ")\n"
                    + "No: paste as text (" + HexDocument.FormatHex(textStart) + (asText.Length > textStart.Length ? " ..." : "") + ")");
                if (answer == HexPasteChoice.Cancel)
                {
                    return null;
                }
                return (answer == HexPasteChoice.Bytes) ? asHex : asText;
            default:
                return Encoding.UTF8.GetBytes(text);
        }
    }
}
