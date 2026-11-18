using SmarTTY.Terminal.Emulation;

namespace SmarTTY.Terminal;

/// <summary>
/// One command run in a Smart Terminal tab: where it was typed, what it was, and the scanner that turns the file names in
/// its output into links (see <see cref="OutputLinkScanner"/>).
/// </summary>
internal class CommandRun
{
    private readonly ICommandRunHost _terminal;

    public readonly string WorkingDirectory;

    public readonly string CommandText;

    public readonly int StartLine;

    public CommandRun(ICommandRunHost terminal, string commandText, string workingDirectory, int startLine)
    {
        _terminal = terminal;
        WorkingDirectory = workingDirectory;
        CommandText = commandText;
        this.StartLine = startLine;
    }

    internal ILinkScanner CreateScanner(bool links, bool highlightDiagnostics)
    {
        return new OutputLinkScanner(_terminal, WorkingDirectory, CommandText, links, highlightDiagnostics, logLevels: false,
            columnSetting: _terminal.CompilerColumns, urls: true);
    }
}

/// <summary>What a <see cref="CommandRun"/> needs from the Smart Terminal tab: the link host of its scanner and the setting for the columns of the compiler.</summary>
internal interface ICommandRunHost : IOutputLinkHost
{
    /// <summary>How the columns after a file name are counted (<see cref="CodeEditing.ColumnUnit.Auto"/> when the settings have none).</summary>
    CodeEditing.ColumnUnit CompilerColumns { get; }
}
