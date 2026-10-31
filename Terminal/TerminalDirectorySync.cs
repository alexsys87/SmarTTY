using SmarTTY.Ssh;
using System;

namespace SmarTTY.Terminal;

/// <summary>
/// Makes the terminal of a Windows host follow the file list: when the user opens a folder in the list, the shell in the tab is
/// asked to change its directory too (as the Smart Terminal does for Unix). It is typed into the shell like the user would, so it is
/// done only when the shell waits at an empty prompt (see <see cref="TerminalDirectoryWatcher.AtPrompt"/>): never into a running
/// program, an ssh inside the shell, or a line the user is typing. A request that comes at the wrong moment waits for the next
/// prompt, and the newest one wins.
/// </summary>
public sealed class TerminalDirectorySync
{
    /// <summary>A command that gets no prompt back in this time is forgotten, so that one lost answer cannot stop the following ones.</summary>
    private static readonly TimeSpan SentTimeout = TimeSpan.FromSeconds(10);

    private readonly TerminalDirectoryWatcher _watcher;

    private readonly Action<string> _type;

    private readonly Func<DateTime> _now;

    private string _pending;

    private DateTime? _sentAt;

    /// <param name="watcher">What the shell shows.</param>
    /// <param name="type">Types text into the shell.</param>
    /// <param name="now">The clock (a test sets it).</param>
    public TerminalDirectorySync(TerminalDirectoryWatcher watcher, Action<string> type, Func<DateTime> now = null)
    {
        _watcher = watcher;
        _type = type;
        _now = now ?? (() => DateTime.UtcNow);
        _watcher.PromptReady += OnPromptReady;
    }

    /// <summary>The user went to a directory in the list (an SFTP path such as "/C:/Users/me").</summary>
    public void Request(string directory)
    {
        string path = WindowsSftpPath.ToWindows(directory);
        if (path == null)
        {
            // "/" above the drives is not a directory a shell can be in.
            return;
        }
        _pending = path;
        TrySend();
    }

    private void OnPromptReady()
    {
        _sentAt = null;
        TrySend();
    }

    private void TrySend()
    {
        if (_pending == null || !_watcher.AtPrompt)
        {
            return;
        }
        if (_sentAt != null && _now() - _sentAt.Value < SentTimeout)
        {
            return;
        }
        string path = _pending;
        _pending = null;
        if (SamePath(path, _watcher.PromptDirectory))
        {
            return;
        }
        string command = BuildCommand(_watcher.Shell, path);
        if (command == null)
        {
            return;
        }
        _sentAt = _now();
        _type(command + "\r");
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals((left ?? "").TrimEnd('\\'), (right ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The command that changes the directory of the shell to the path: "cd /d" for cmd.exe (it is the only way to change the
    /// drive), Set-Location -LiteralPath for PowerShell (a path with [ ] in it is not a pattern there). Null when the path has a
    /// character that cannot be passed safely: cmd.exe expands %NAME% even in quotes, and a quote ends the quoting.
    /// </summary>
    public static string BuildCommand(PromptShell shell, string path)
    {
        if (path.IndexOfAny(new[] { '%', '"', '\r', '\n', '\t', '\0' }) >= 0)
        {
            return null;
        }
        switch (shell)
        {
            case PromptShell.PowerShell:
                // PowerShell reads the typographic single quotes as quotes too, and doubles any of them to take it literally.
                string quoted = path;
                foreach (char quote in new[] { '\'', '\u2018', '\u2019', '\u201A', '\u201B' })
                {
                    quoted = quoted.Replace(quote.ToString(), quote.ToString() + quote);
                }
                return "Set-Location -LiteralPath '" + quoted + "'";
            case PromptShell.Cmd:
                return "cd /d \"" + path + "\"";
            default:
                return null;
        }
    }
}
