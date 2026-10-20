using SmarTTY.Ssh;
using SmarTTY.Controls;
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

    private List<string> _history;

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
        new Thread(LoadHistoryAndPath).Start();
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
            _history = _sshSession.RunCommandChecked("cat " + _context.VTSettings.CompletionTriggers.HistoryFileName, requireSuccess: true);
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
        return _history?.ToArray();
    }

    public static string EscapeForShell(string text)
    {
        return SmarTTY.IO.RemotePathUtils.EscapeForDoubleQuotes(text);
    }

    public void AppendToHistory(string text)
    {
        if (text == null)
        {
            return;
        }
        string item = "echo \"" + EscapeForShell(text) + "\" >> " + _context.VTSettings.CompletionTriggers.HistoryFileName;
        lock (_historyQueue)
        {
            _historyQueue.Enqueue(item);
            if (!_historyWriterBusy)
            {
                new Thread(ProcessHistoryQueue).Start();
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
            command = _historyQueue.Dequeue();
            _historyWriterBusy = true;
        }
        try
        {
            _historyChannel?.Dispose();
            _historyChannel = _sshSession.OpenShellChannel(command);
            _historyChannel.Finished += (object sender) =>
            {
                _historyWriterBusy = false;
                new Thread(ProcessHistoryQueue).Start();
            };
            _historyChannel.PollingEnabled = true;
        }
        catch
        {
            _historyChannel = null;
            _historyWriterBusy = false;
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
