using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// The state that the escape sequences of a program set in the terminal and that is not part of the screen: the DEC private modes, the
/// application keypad and cursor keys, the character sets, the cursor that ESC 7 saved, and the start of an escape sequence that has not
/// arrived whole.
/// </summary>
internal sealed class TerminalModes
{
    /// <summary>The character sets (the line drawing set of ESC ( 0 and the shifts between them).</summary>
    public readonly DecCharsets Charsets = new DecCharsets();

    /// <summary>The modes set with CSI ? Pn h and the values saved with CSI ? Pn s.</summary>
    public readonly PrivateModeTable PrivateModes = new PrivateModeTable();

    /// <summary>ESC = (true) and ESC > (false): the keys of the numeric keypad send escape sequences.</summary>
    public bool ApplicationKeypad;

    /// <summary>CSI ? 1 h: the arrow keys send ESC O A (and so on) instead of ESC [ A.</summary>
    public bool ApplicationCursorKeys;

    /// <summary>The cursor position that ESC 7, CSI s and CSI ? 1048 h saved.</summary>
    public GridPoint SavedCursor;

    /// <summary>The style that was saved with <see cref="SavedCursor"/>.</summary>
    public CellStyle SavedStyle;

    /// <summary>The beginning of an escape sequence that the last chunk of output ended in the middle of; null when there is none.</summary>
    public string IncompleteEscape;

    /// <summary>The part of a reset (ESC c, CSI ! p) that is not about the screen: back to the state of a new terminal. The incomplete escape is not touched.</summary>
    public void Reset()
    {
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        PrivateModes.Clear();
        SavedCursor = default;
        SavedStyle = CellStyle.Default;
        Charsets.Reset();
    }
}
