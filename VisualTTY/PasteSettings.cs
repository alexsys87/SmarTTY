using SmarTTY.Utilities;

namespace VisualTTY;

/// <summary>
/// How text pasted into a terminal is cleaned and checked (see SmarTTY.Terminal.PasteSanitizer and
/// SmarTTY.Controls.PasteGuard).
/// </summary>
public class PasteSettings
{
    /// <summary>What a line break of the pasted text is sent as.</summary>
    public enum LineBreakMode
    {
        [EnumDisplayText("Automatic (like Enter of the terminal)")]
        Auto,
        [EnumDisplayText("CR (the Enter key)")]
        CR,
        [EnumDisplayText("LF")]
        LF,
        [EnumDisplayText("CR LF")]
        CRLF
    }

    /// <summary>Removes escape sequences and control characters (protection from text that acts on the terminal).</summary>
    public bool RemoveControlCharacters { get; set; } = true;

    /// <summary>Replaces curly quotes, long dashes, no-break spaces and the like with ASCII characters.</summary>
    public bool ReplaceTypographicCharacters { get; set; }

    /// <summary>Removes the line break at the end, so that the last pasted line waits for Enter.</summary>
    public bool TrimTrailingNewline { get; set; }

    /// <summary>
    /// Line break of the pasted text. Automatic: CR in SSH tabs (what Enter sends), the line ending of the connection in
    /// serial and TCP tabs.
    /// </summary>
    public LineBreakMode LineBreak { get; set; }

    /// <summary>Asks before pasting several lines that run at once (not asked while the program uses bracketed paste).</summary>
    public bool ConfirmMultiline { get; set; } = true;

    /// <summary>The number of lines from which a paste is confirmed; a single line that ends with a line break is confirmed too.</summary>
    public int ConfirmLineThreshold { get; set; } = 2;

    /// <summary>Asks before pasting this many characters or more; 0 turns the size check off.</summary>
    public int ConfirmSizeThreshold { get; set; } = 5000;

    /// <summary>Warns about dangerous commands (sudo, curl | sh, rm -rf, ...) in text that runs at once. Always asks.</summary>
    public bool WarnDangerous { get; set; } = true;

    /// <summary>The line break to send: <paramref name="automatic"/> unless a mode is chosen.</summary>
    public string ResolveLineBreak(string automatic)
    {
        switch (LineBreak)
        {
            case LineBreakMode.CR:
                return "\r";
            case LineBreakMode.LF:
                return "\n";
            case LineBreakMode.CRLF:
                return "\r\n";
            default:
                return automatic;
        }
    }
}
