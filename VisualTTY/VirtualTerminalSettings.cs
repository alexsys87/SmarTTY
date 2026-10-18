
namespace VisualTTY;

public class VirtualTerminalSettings
{
    public VirtualTerminalMode DefaultMode { get; set; }

    public AutoCompletionTriggers CompletionTriggers { get; set; } = new AutoCompletionTriggers();

    public string DefaultShell { get; set; } = "bash";

    public bool InteractiveModeWrapper { get; set; } = true;

    public bool ShowFileExplorer { get; set; } = true;

    public string AutoLinkExtensions { get; set; } = ".c;.cpp;.h;.sh;.txt;.js";
}
