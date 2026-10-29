using SmarTTY.Ssh;
using VisualTTY;
using SmarTTY.Completion;

namespace SmarTTY.Terminal;

public interface ITerminalSessionContext
{
    SshSession Session { get; }

    string TitleWithUserName { get; }

    VirtualTerminalSettings VTSettings { get; }

    bool IsRoot { get; }

    /// <summary>
    /// True when the connection saves files with sudo without asking about the missing write permission
    /// (the sudo password is still asked when the file is saved).
    /// </summary>
    bool AutoConfirmSudo { get; }

    ShellCompletionAnalyzer ProvideAnalyzer();
}
