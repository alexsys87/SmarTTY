
namespace VisualTTY;

public class VirtualTerminalSettings
{
    public VirtualTerminalMode DefaultMode { get; set; }

    public AutoCompletionTriggers CompletionTriggers { get; set; } = new AutoCompletionTriggers();

    public string DefaultShell { get; set; } = "bash";

    public bool InteractiveModeWrapper { get; set; } = true;

    /// <summary>Show "[exit code N]" after a command that fails (and a note when the connection was lost).</summary>
    public bool ShowExitCodes { get; set; } = true;

    public bool ShowFileExplorer { get; set; } = true;

    /// <summary>
    /// Byte mode for programs run in Smart Terminal tabs when the shell counts UTF-8 bytes (BusyBox without Unicode
    /// support, OpenWrt); the command line itself is always edited locally. See GlobalSettings.ByteOrientedLineEditing.
    /// </summary>
    public GlobalSettings.ByteOrientedLineEditingMode ByteOrientedLineEditing { get; set; }

    public string AutoLinkExtensions { get; set; } = ".c;.cpp;.h;.sh;.txt;.js";
}
