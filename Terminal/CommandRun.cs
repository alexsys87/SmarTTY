using SmarTTY.Controls;

namespace SmarTTY.Terminal;

/// <summary>
/// One command run in a Smart Terminal tab: where it was typed, what it was, and the scanner that turns the file names in
/// its output into links (see <see cref="OutputLinkScanner"/>).
/// </summary>
internal class CommandRun
{
    private readonly SshTerminalControl _terminal;

    public readonly string WorkingDirectory;

    public readonly string CommandText;

    public readonly int StartLine;

    public int UnusedValue { get; private set; }

    public CommandRun(SshTerminalControl sshTerminalControl, string commandText, string workingDirectory, int startLine)
    {
        _terminal = sshTerminalControl;
        WorkingDirectory = workingDirectory;
        CommandText = commandText;
        this.StartLine = startLine;
    }

    internal TerminalEmulatorBase.ILinkScanner CreateScanner(bool links, bool highlightDiagnostics)
    {
        return new OutputLinkScanner(_terminal, WorkingDirectory, CommandText, links, highlightDiagnostics);
    }
}
