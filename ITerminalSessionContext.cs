using SmarTTY.Ssh;
using VisualTTY;

namespace SmarTTY;

public interface ITerminalSessionContext
{
    SshSession Session { get; }

    string TitleWithUserName { get; }

    VirtualTerminalSettings VTSettings { get; }

    bool IsRoot { get; }

    ShellCompletionAnalyzer ProvideAnalyzer();
}
