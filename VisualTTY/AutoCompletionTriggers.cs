
namespace VisualTTY;

public class AutoCompletionTriggers
{
    public bool CtrlSpace { get; set; } = true;

    public bool Slash { get; set; } = true;

    public bool Space { get; set; } = true;

    public bool FirstCharacterInWord { get; set; } = true;

    public string HistoryFileName { get; set; } = "~/.bash_history";
}
