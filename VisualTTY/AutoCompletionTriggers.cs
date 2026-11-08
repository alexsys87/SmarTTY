
namespace VisualTTY;

public class AutoCompletionTriggers
{
    public bool CtrlSpace { get; set; } = true;

    public bool Slash { get; set; } = true;

    public bool Space { get; set; } = true;

    public bool FirstCharacterInWord { get; set; } = false;

    /// <summary>
    /// Show the rest of a previously typed command in grey after the caret while typing in a Smart Terminal tab; Right or End
    /// at the end of the line take it, Ctrl+Right takes its next word, Esc hides it.
    /// </summary>
    public bool HistorySuggestions { get; set; } = true;

    public string HistoryFileName { get; set; } = "~/.bash_history";
}
