using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualTTY;

namespace SmarTTY;

public class ShellCompletionAnalyzer : IDisposable
{

    private ITerminalSessionContext _context;

    private SshSession _sshSession;

    // Commands of the history file followed by the ones typed in this session (all tabs); guarded by itself.
    private readonly List<string> _history = new List<string>();

    private Dictionary<string, HashSet<string>> _executablesByDir = new Dictionary<string, HashSet<string>>();

    private Queue<string> _historyQueue = new Queue<string>();

    private SshShellChannel _historyChannel;

    private bool _historyWriterBusy;

    private List<string> _pathDirs;

    private CommandCompletionDatabase _completionDatabase;

    public CommandCompletionDatabase CompletionDatabase
    {
        get
        {
            if (_completionDatabase == null)
            {
                try
                {
                    _completionDatabase = CommandCompletionDatabase.Load();
                }
                catch
                {
                    _completionDatabase = new CommandCompletionDatabase();
                }
            }
            return _completionDatabase;
        }
    }

    public ShellCompletionAnalyzer(ITerminalSessionContext terminalSessionContext, SshSession sshSession)
    {
        this._context = terminalSessionContext;
        _sshSession = sshSession;
        new Thread(LoadHistoryAndPath)
        {
            IsBackground = true
        }.Start();
    }

    public void Dispose()
    {
        try
        {
            _historyChannel?.Dispose();
        }
        catch
        {
        }
    }

    private void LoadHistoryAndPath()
    {
        try
        {
            List<string> lines = _sshSession.RunCommandChecked("cat " + _context.VTSettings.CompletionTriggers.HistoryFileName, requireSuccess: true);
            if (lines != null)
            {
                // Time stamps of bash (HISTTIMEFORMAT) and zsh (extended history) are not commands.
                List<string> commands = lines.Select(ShellCommandLine.ParseHistoryLine).Where((string line) => line != null).ToList();
                lock (_history)
                {
                    // Commands typed while the file was being read come after it.
                    _history.InsertRange(0, commands);
                }
            }
        }
        catch
        {
        }
        try
        {
            _pathDirs = SplitPath(_sshSession.RunCommandChecked("echo $PATH", requireSuccess: true, "xterm"));
            CollectExecutables(_pathDirs);
        }
        catch
        {
        }
    }

    private IEnumerable<string> CollectExecutables(IEnumerable<string> items)
    {
        if (items == null)
        {
            return new List<string>();
        }
        string[] unprocessedDirs;
        lock (this._executablesByDir)
        {
            unprocessedDirs = items.Where((string dir) => !this._executablesByDir.ContainsKey(dir)).ToArray();
        }
        lock (this._executablesByDir)
        {
            // Other threads read this dictionary under the same lock.
            foreach (string dir in unprocessedDirs)
            {
                this._executablesByDir[dir] = new HashSet<string>();
            }
        }
        List<string> findOutput = new List<string>();
        if (unprocessedDirs.Length != 0)
        {
            string findCommand = "find -L " + string.Join(" ", unprocessedDirs.Select((string dir) => SmarTTY.IO.RemotePathUtils.ShellQuote(dir)).ToArray()) + " -maxdepth 1 -perm -111 -type f";
            findOutput = _sshSession.RunCommandChecked(findCommand, requireSuccess: false);
            if (findOutput == null)
            {
                throw new Exception("Failed to list files in $PATH");
            }
        }
        lock (this._executablesByDir)
        {
            string[] sortedDirs = items.OrderBy((string dir) => -dir.Length).ToArray();
            foreach (string filePath in findOutput)
            {
                if (!filePath.StartsWith("/"))
                {
                    continue;
                }
                foreach (string dir in sortedDirs)
                {
                    if (filePath.StartsWith(dir))
                    {
                        if (!this._executablesByDir.TryGetValue(dir, out var executablesSet))
                        {
                            executablesSet = (this._executablesByDir[dir] = new HashSet<string>());
                        }
                        executablesSet.Add(filePath);
                        break;
                    }
                }
            }
            HashSet<string> resultSet = new HashSet<string>();
            foreach (string dir in items)
            {
                if (!this._executablesByDir.TryGetValue(dir, out var executablesSet))
                {
                    continue;
                }
                foreach (string executablePath in executablesSet)
                {
                    resultSet.Add(executablePath);
                }
            }
            return resultSet;
        }
    }

    public string[] GetHistory()
    {
        lock (_history)
        {
            return _history.ToArray();
        }
    }

    public static string EscapeForShell(string text)
    {
        return SmarTTY.IO.RemotePathUtils.EscapeForDoubleQuotes(text);
    }

    public void AppendToHistory(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        lock (_history)
        {
            // Other Smart Terminal tabs of the session see the command as well.
            _history.Add(text);
        }
        // printf rather than echo: echo may interpret backslashes or take "-n"/"-e" at the start of a command as options.
        string item = "printf '%s\\n' " + SmarTTY.IO.RemotePathUtils.ShellQuote(text) + " >> " + _context.VTSettings.CompletionTriggers.HistoryFileName;
        lock (_historyQueue)
        {
            _historyQueue.Enqueue(item);
            if (!_historyWriterBusy)
            {
                new Thread(ProcessHistoryQueue)
                {
                    IsBackground = true
                }.Start();
            }
        }
    }

    private void ProcessHistoryQueue()
    {
        string command = null;
        lock (_historyQueue)
        {
            if (_historyWriterBusy || _historyQueue.Count == 0)
            {
                return;
            }
            // Everything queued so far is written by one command: a channel per history line was slow, and a failed
            // write used to leave the rest of the queue waiting for the next command.
            command = string.Join("; ", _historyQueue.ToArray());
            _historyQueue.Clear();
            _historyWriterBusy = true;
        }
        try
        {
            _historyChannel?.Dispose();
            _historyChannel = _sshSession.OpenShellChannel(command);
            _historyChannel.Finished += (object sender) =>
            {
                lock (_historyQueue)
                {
                    _historyWriterBusy = false;
                }
                new Thread(ProcessHistoryQueue)
                {
                    IsBackground = true
                }.Start();
            };
            _historyChannel.PollingEnabled = true;
        }
        catch
        {
            _historyChannel = null;
            lock (_historyQueue)
            {
                _historyWriterBusy = false;
            }
        }
    }

    private static List<string> SplitPath(List<string> pathLines)
    {
        List<string> items = new List<string>();
        if (pathLines != null)
        {
            foreach (string pathLine in pathLines)
            {
                string[] parts = pathLine.Split(':');
                foreach (string part in parts)
                {
                    items.Add(part);
                }
            }
        }
        return items;
    }

    public IEnumerable<string> GetExecutables(SshTerminalControl sshTerminalControl)
    {
        List<string> rawPathLines = _pathDirs ?? new List<string>();
        if (rawPathLines.FirstOrDefault((string path) => !path.StartsWith("/")) != null || rawPathLines.Count == 0 || sshTerminalControl.HasEnvironment)
        {
            rawPathLines = _sshSession.RunCommandChecked(sshTerminalControl.BuildRemoteCommand("echo $PATH"), requireSuccess: true, "xterm");
        }
        List<string> pathDirectories = SplitPath(rawPathLines);
        return CollectExecutables(pathDirectories);
    }
}
