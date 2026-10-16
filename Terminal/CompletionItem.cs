using VisualTTY;

namespace SmarTTY.Terminal;

public struct CompletionItem
{
    public string Text;

    public VirtualTerminalPopupItemIcon Icon;

    public VirtualTerminalPopupSummary Summary;

    public string InsertText;

    public string Description;

    public override string ToString()
    {
        return Text;
    }
}
