
namespace VisualTTY;

public class AutoCompletionTriggers
{
    public bool CtrlSpace { get; set; } = true;

    public bool Slash { get; set; } = true;

    public bool Space { get; set; } = true;

    public bool FirstCharacterInWord { get; set; } = false;

    /// <summary>
    /// Show the rest of a previously typed command in grey after the caret while typing in a Smart Terminal tab or a COM/TCP
    /// terminal; Right or End at the end of the line take it, Ctrl+Right takes its next word, Esc hides it.
    /// </summary>
    public bool HistorySuggestions { get; set; } = true;

    /// <summary>
    /// Up and Down on a line with text show only the commands that start with it (history-search-backward in bash); on an
    /// empty line they go through every command as before. Smart Terminal tabs and COM/TCP terminals.
    /// </summary>
    public bool HistoryPrefixSearch { get; set; } = true;

    public string HistoryFileName { get; set; } = "~/.bash_history";
}
