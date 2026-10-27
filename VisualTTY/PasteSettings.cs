using SmarTTY.Utilities;
using System.Collections.Generic;

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

    /// <summary>What a mouse button does in a terminal.</summary>
    public enum MouseAction
    {
        [EnumDisplayText("Context menu")]
        Menu,
        [EnumDisplayText("Paste")]
        Paste,
        [EnumDisplayText("Copy the selection, paste when nothing is selected")]
        CopyOrPaste
    }

    /// <summary>What Ctrl+Shift+V does in a terminal.</summary>
    public enum ShortcutAction
    {
        [EnumDisplayText("Paste")]
        Paste,
        [EnumDisplayText("Menu with the history and the snippets")]
        HistoryMenu
    }

    /// <summary>What happens to characters that the encoding of a COM/TCP/Telnet session cannot represent.</summary>
    public enum UnmappableMode
    {
        [EnumDisplayText("Ask (they would be sent as ?)")]
        Ask,
        [EnumDisplayText("Send them as ?")]
        QuestionMark
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
    /// <summary>What the right mouse button does in a terminal (Shift+right button always opens the menu).</summary>
    public MouseAction RightClick { get; set; } = MouseAction.Menu;

    /// <summary>The middle mouse button pastes.</summary>
    public bool MiddleClickPastes { get; set; } = true;

    /// <summary>What Ctrl+Shift+V does; the history menu is also on Ctrl+Shift+Insert and in the terminal menu.</summary>
    public ShortcutAction CtrlShiftV { get; set; } = ShortcutAction.Paste;

    /// <summary>
    /// Remembers pasted texts for the history menu. The history lives in memory only (never in a file) and is gone when
    /// SmarTTY is closed.
    /// </summary>
    public bool KeepHistory { get; set; } = true;

    /// <summary>The number of pasted texts the history keeps (1 to 50).</summary>
    public int HistorySize { get; set; } = 20;

    /// <summary>Texts saved by the user, offered by the paste menu.</summary>
    public List<PasteSnippet> Snippets { get; set; } = new List<PasteSnippet>();

    /// <summary>Files in the clipboard (copied in Explorer) are offered for upload instead of being ignored by Paste.</summary>
    public bool OfferClipboardFiles { get; set; } = true;

    /// <summary>In the HEX view of a COM/TCP terminal a pasted text such as "DE AD BE EF" is sent as bytes.</summary>
    public bool HexViewPastesBytes { get; set; } = true;

    /// <summary>What to do with characters that the session encoding cannot represent.</summary>
    public UnmappableMode UnmappableCharacters { get; set; } = UnmappableMode.Ask;

    /// <summary>The number of history entries to keep, within the allowed range.</summary>
    public int EffectiveHistorySize => KeepHistory ? System.Math.Max(1, System.Math.Min(HistorySize, 50)) : 0;

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

/// <summary>A text saved for the paste menu.</summary>
public class PasteSnippet
{
    public string Name { get; set; }

    public string Text { get; set; }
}
