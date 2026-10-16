using ManagedLibSSH2;
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
            _history = _sshSession.RunCommandChecked("cat " + _context.VTSettings.CompletionTriggers.HistoryFileName, flag: true);
        }
        catch
        {
        }
        try
        {
            _pathDirs = SplitPath(_sshSession.RunCommandChecked("echo $PATH", flag: true, "xterm"));
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
        string[] array;
        lock (this._executablesByDir)
        {
            array = items.Where((string key2) => !this._executablesByDir.ContainsKey(key2)).ToArray();
        }
        string[] array2 = array;
        foreach (string key in array2)
        {
            this._executablesByDir[key] = new HashSet<string>();
        }
        List<string> list = new List<string>();
        if (array.Length != 0)
        {
            string text = "find -L " + string.Join(" ", array.Select((string text3) => "\"" + text3 + "\"").ToArray()) + " -maxdepth 1 -perm -111 -type f";
            list = _sshSession.RunCommandChecked(text, flag: false);
            if (list == null)
            {
                throw new Exception("Failed to list files in $PATH");
            }
        }
        lock (this._executablesByDir)
        {
            string[] array3 = items.OrderBy((string text3) => -text3.Length).ToArray();
            foreach (string item in list)
            {
                if (!item.StartsWith("/"))
                {
                    continue;
                }
                array2 = array3;
                foreach (string text2 in array2)
                {
                    if (item.StartsWith(text2))
                    {
                        if (!this._executablesByDir.TryGetValue(text2, out var value))
                        {
                            value = (this._executablesByDir[text2] = new HashSet<string>());
                        }
                        value.Add(item);
                        break;
                    }
                }
            }
            HashSet<string> hashSet2 = new HashSet<string>();
            foreach (string item2 in items)
            {
                if (!this._executablesByDir.TryGetValue(item2, out var value2))
                {
                    continue;
                }
                foreach (string item3 in value2)
                {
                    hashSet2.Add(item3);
                }
            }
            return hashSet2;
        }
    }

    public string[] GetHistory()
    {
        return _history?.ToArray();
    }

    public static string EscapeForShell(string text)
    {
        if (text == null)
        {
            return "";
        }
        text = text.Replace("$", "\\$").Replace("\"", "\\\"");
        return text;
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
        string text = null;
        lock (_historyQueue)
        {
            if (_historyWriterBusy || _historyQueue.Count == 0)
            {
                return;
            }
            text = _historyQueue.Dequeue();
            _historyWriterBusy = true;
        }
        try
        {
            _historyChannel?.Dispose();
            _historyChannel = _sshSession.OpenShellChannel(text);
            _historyChannel.Finished += (object obj2) =>
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

    private static List<string> SplitPath(List<string> list2)
    {
        List<string> list = new List<string>();
        if (list2 != null)
        {
            foreach (string item2 in list2)
            {
                string[] array = item2.Split(':');
                foreach (string item in array)
                {
                    list.Add(item);
                }
            }
        }
        return list;
    }

    public IEnumerable<string> GetExecutables(SshTerminalControl sshTerminalControl)
    {
        List<string> list = _pathDirs ?? new List<string>();
        if (list.FirstOrDefault((string text) => !text.StartsWith("/")) != null || list.Count == 0 || sshTerminalControl.HasEnvironment)
        {
            list = _sshSession.RunCommandChecked(sshTerminalControl.BuildRemoteCommand("echo $PATH"), flag: true, "xterm");
        }
        List<string> list2 = SplitPath(list);
        return CollectExecutables(list2);
    }
}
