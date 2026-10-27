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

    /// <summary>What a slow paste waits for after each line before it sends the next one.</summary>
    public enum SlowWaitMode
    {
        [EnumDisplayText("Nothing (only the pauses)")]
        None,
        [EnumDisplayText("The echo of the line")]
        Echo,
        [EnumDisplayText("The prompt")]
        Prompt
    }

    /// <summary>
    /// COM, TCP and Telnet tabs: every paste is sent slowly, as "Paste slowly" does (otherwise only that command is).
    /// </summary>
    public bool SlowPasteAlways { get; set; }

    /// <summary>Slow paste: characters sent at once (0: a whole line).</summary>
    public int SlowChunkSize { get; set; } = 16;

    /// <summary>Slow paste: pause in milliseconds after each block of characters.</summary>
    public int SlowCharDelayMs { get; set; } = 10;

    /// <summary>Slow paste: pause in milliseconds after each line.</summary>
    public int SlowLineDelayMs { get; set; } = 100;

    /// <summary>Slow paste: what to wait for after a line.</summary>
    public SlowWaitMode SlowWait { get; set; }

    /// <summary>Slow paste: the prompt of the device to wait for (for example "=>" in U-Boot).</summary>
    public string SlowPrompt { get; set; } = "=>";

    /// <summary>Slow paste: how long to wait for the echo or the prompt, in milliseconds.</summary>
    public int SlowWaitTimeoutMs { get; set; } = 5000;

    /// <summary>Paste line by line (Smart Terminal): stop when a command ends with a non-zero exit code.</summary>
    public bool LineByLineStopOnError { get; set; } = true;

    /// <summary>
    /// SSH tabs: from this size in kilobytes a paste is offered to be uploaded as a file (scp) instead of being typed into the
    /// terminal; 0 turns the offer off.
    /// </summary>
    public int UploadOfferThresholdKB { get; set; } = 256;

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
