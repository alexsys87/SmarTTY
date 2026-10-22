using SmarTTY.Utilities;

namespace VisualTTY;

public class VirtualTerminalSettings
{
    public VirtualTerminalMode DefaultMode { get; set; }

    public AutoCompletionTriggers CompletionTriggers { get; set; } = new AutoCompletionTriggers();

    /// <summary>How the shell of the interactive wrapper is chosen (see SmarTTY.Terminal.ShellSelection).</summary>
    public enum ShellSelectionMode
    {
        [EnumDisplayText("Detect (bash, or ash on OpenWrt)")]
        Auto,
        [EnumDisplayText("Manual")]
        Manual
    }

    private ShellSelectionMode? _shellMode;

    /// <summary>
    /// Automatic: per connection, the login shell if it is bash or ash, otherwise bash, ash or sh, whichever is installed.
    /// Manual: <see cref="DefaultShell"/>. Settings saved before this option existed have no value for it; a shell other
    /// than the old default "bash" was typed in by the user there, so it stays in use.
    /// </summary>
    public ShellSelectionMode ShellMode
    {
        get => _shellMode ?? ((string.IsNullOrWhiteSpace(DefaultShell) || DefaultShell.Trim() == "bash") ? ShellSelectionMode.Auto : ShellSelectionMode.Manual);
        set => _shellMode = value;
    }

    /// <summary>The shell of the manual mode, used as typed (e.g. "bash", "/bin/ash" or "bash --norc").</summary>
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
