using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualTTY;

namespace SmarTTY.Completion;

public class ShellCompletionAnalyzer : IDisposable
{

    private ITerminalSessionContext _context;

    private SshSession _sshSession;

    // Commands of the history file followed by the ones typed in this session (all tabs); guarded by itself.
    private readonly List<string> _history = new List<string>();

    private Dictionary<string, HashSet<string>> _executablesByDir = new Dictionary<string, HashSet<string>>();

    // A change of the history file: a command to append, or commands to remove (the "Command history" window).
    private sealed class HistoryFileJob
    {
        public string Append;

        public List<string> Remove;

        public Action<bool> Done;
    }

    // Changes waiting to be made to the history file, by one background thread at a time and in the order they were made.
    private Queue<HistoryFileJob> _historyQueue = new Queue<HistoryFileJob>();

    // Commands removed in this session (guarded by _history): the history file may still be being read, and the commands
    // it had must not come back from it. A command that is typed again is taken off this list.
    private readonly HashSet<string> _removedCommands = new HashSet<string>(StringComparer.Ordinal);

    // Changes with every change of _history (guarded by it).
    private int _historyVersion;

    // Set once the history file has been read (or could not be read).
    private readonly ManualResetEventSlim _historyLoaded = new ManualResetEventSlim(false);

    private volatile bool _disposed;

    private bool _historyWriterBusy;

    private List<string> _pathDirs;

    private CommandCompletionDatabase _completionDatabase;

    // The ChangeCounter at which _completionDatabase was loaded: the editor of the database bumps it on save.
    private int _completionDatabaseVersion;

    // What the host answered to ShellSelection.ProbeCommand (null if the probe failed), set before _shellProbed.
    private ShellSelection.ProbeResult _shellProbe;

    private readonly ManualResetEventSlim _shellProbed = new ManualResetEventSlim(false);

    /// <summary>How long a command typed right after connecting waits for the shell probe before it runs without the wrapper.</summary>
    private static readonly TimeSpan ShellProbeWait = TimeSpan.FromSeconds(3);

    /// <summary>How long the background work (history, definitions) waits for the shell probe.</summary>
    private static readonly TimeSpan ShellProbeBackgroundWait = TimeSpan.FromSeconds(15);

    public CommandCompletionDatabase CompletionDatabase
    {
        get
        {
            int version = CommandCompletionDatabase.ChangeCounter;
            if (_completionDatabase == null || _completionDatabaseVersion != version)
            {
                _completionDatabaseVersion = version;
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
        _disposed = true;
    }

    /// <summary>
    /// The shell that runs commands in the interactive wrapper: in the automatic mode the one the probe of this connection
    /// found (bash, or ash on OpenWrt), in the manual mode the one of the settings. Waits for the probe at most
    /// <paramref name="wait"/>; null when no shell is known, the command then runs without the wrapper.
    /// </summary>
    public ShellSelection.Choice GetInteractiveShell(TimeSpan wait)
    {
        _shellProbed.Wait(wait);
        ShellSelection.ProbeResult probe = _shellProbed.IsSet ? _shellProbe : null;
        VirtualTerminalSettings settings = _context.VTSettings;
        if (settings.ShellMode == VirtualTerminalSettings.ShellSelectionMode.Manual)
        {
            ShellSelection.Choice manual = ShellSelection.ChooseManual(settings.DefaultShell, probe);
            if (manual != null)
            {
                return manual;
            }
        }
        return ShellSelection.ChooseAutomatic(probe);
    }

    /// <inheritdoc cref="GetInteractiveShell(TimeSpan)"/>
    public ShellSelection.Choice GetInteractiveShell()
    {
        return GetInteractiveShell(ShellProbeWait);
    }

    /// <summary>The history file of the shell (~/.ash_history on OpenWrt), unless another file is set in the settings.</summary>
    private string GetHistoryFile()
    {
        return ShellSelection.GetHistoryFile(_context.VTSettings.CompletionTriggers.HistoryFileName, GetInteractiveShell(ShellProbeBackgroundWait));
    }

    /// <summary>The history file of the shell, or null while the shell of the host is not known yet (it does not wait).</summary>
    public string HistoryFileIfKnown => _shellProbed.IsSet ? GetHistoryFile() : null;

    private void ProbeShell()
    {
        try
        {
            _shellProbe = ShellSelection.ParseProbe(_sshSession.RunCommandChecked(ShellSelection.ProbeCommand, requireSuccess: false));
        }
        catch
        {
        }
        finally
        {
            _shellProbed.Set();
        }
    }

    private void LoadHistoryAndPath()
    {
        // First of all: the history file and the wrapper of the commands depend on the shell.
        ProbeShell();
        try
        {
            List<string> lines = _sshSession.RunCommandChecked("cat " + GetHistoryFile(), requireSuccess: true);
            if (lines != null)
            {
                // Time stamps of bash (HISTTIMEFORMAT) and zsh (extended history) are not commands.
                List<string> commands = lines.Select(ShellCommandLine.ParseHistoryLine).Where((string line) => line != null).ToList();
                lock (_history)
                {
                    commands.RemoveAll(_removedCommands.Contains);
                    // Commands typed while the file was being read come after it.
                    _history.InsertRange(0, commands);
                    _historyVersion++;
                }
            }
        }
        catch
        {
        }
        finally
        {
            _historyLoaded.Set();
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
            // Other threads read this dictionary under the same lock. The directories count as processed only now: a
            // failed find (a lost connection) must leave them to be listed on the next call, not cached as empty.
            foreach (string dir in unprocessedDirs)
            {
                if (!this._executablesByDir.ContainsKey(dir))
                {
                    this._executablesByDir[dir] = new HashSet<string>();
                }
            }
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

    private readonly object _definitionsLock = new object();

    private CompletionSources.ShellDefinitions _shellDefinitions;

    private List<CompletionSources.HostEntry> _hosts;

    /// <summary>
    /// Aliases, functions and variables of the shell the commands run in, loaded once per session. With the interactive
    /// wrapper (Smart Terminal Settings) that is the interactive shell, which reads .bashrc; without it only the
    /// environment is known, since aliases and functions of .bashrc are not available to the commands then.
    /// </summary>
    internal CompletionSources.ShellDefinitions GetShellDefinitions(SshTerminalControl sshTerminalControl)
    {
        lock (_definitionsLock)
        {
            if (_shellDefinitions != null)
            {
                return _shellDefinitions;
            }
            List<string> output;
            if (_context.VTSettings.InteractiveModeWrapper)
            {
                // Loaded once per session, so it waits for the shell rather than run without the wrapper on a slow server.
                _shellProbed.Wait(ShellProbeBackgroundWait);
                string command = sshTerminalControl.BuildRemoteCommand(CompletionSources.BuildShellDefinitionsScript(), interactive: true);
                // A .bashrc that waits for input must not keep the helper command (and with it the PTY gate) busy.
                string quoted = SmarTTY.IO.RemotePathUtils.ShellQuote(command);
                output = _sshSession.RunCommandChecked("if command -v timeout >/dev/null 2>&1; then timeout 10 sh -c " + quoted + "; else sh -c " + quoted + "; fi </dev/null", requireSuccess: false);
            }
            else
            {
                output = _sshSession.RunCommandChecked(sshTerminalControl.BuildRemoteCommand("env", interactive: false), requireSuccess: false);
                output?.Insert(0, CompletionSources.VariablesMarker);
            }
            _shellDefinitions = CompletionSources.ParseShellDefinitions(output);
            return _shellDefinitions;
        }
    }

    /// <summary>Host names from ~/.ssh/config, ~/.ssh/known_hosts and /etc/hosts of the server, loaded once per session.</summary>
    internal List<CompletionSources.HostEntry> GetHosts()
    {
        lock (_definitionsLock)
        {
            if (_hosts == null)
            {
                _hosts = CompletionSources.ParseHostFiles(_sshSession.RunCommandChecked(CompletionSources.BuildHostFilesCommand(), requireSuccess: false));
            }
            return _hosts;
        }
    }

    /// <summary>
    /// Changes whenever the history does: the file has been read, a command was typed in any tab, or commands were removed.
    /// </summary>
    public int HistoryVersion
    {
        get
        {
            lock (_history)
            {
                return _historyVersion;
            }
        }
    }

    public string[] GetHistory()
    {
        lock (_history)
        {
            return _history.ToArray();
        }
    }

    /// <summary>The history, oldest first, once the history file has been read or <paramref name="wait"/> has passed.</summary>
    public string[] GetHistory(TimeSpan wait)
    {
        _historyLoaded.Wait(wait);
        return GetHistory();
    }

    /// <summary>
    /// Removes <paramref name="commands"/> from the history of the session (every tab sees it at once) and, in the
    /// background after the changes queued before, from the history file on the server. <paramref name="done"/> is
    /// called on that background thread with whether the file was changed (true also when there was no file).
    /// </summary>
    public void RemoveFromHistory(IEnumerable<string> commands, Action<bool> done = null)
    {
        List<string> list = (commands ?? Enumerable.Empty<string>()).Where((string command) => !string.IsNullOrEmpty(command)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            done?.Invoke(true);
            return;
        }
        HashSet<string> set = new HashSet<string>(list, StringComparer.Ordinal);
        lock (_history)
        {
            _history.RemoveAll(set.Contains);
            _removedCommands.UnionWith(set);
            _historyVersion++;
        }
        QueueHistoryFileJob(new HistoryFileJob
        {
            Remove = list,
            Done = done
        });
    }

    private void QueueHistoryFileJob(HistoryFileJob job)
    {
        lock (_historyQueue)
        {
            _historyQueue.Enqueue(job);
            if (!_historyWriterBusy)
            {
                _historyWriterBusy = true;
                new Thread(ProcessHistoryQueue)
                {
                    IsBackground = true
                }.Start();
            }
        }
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
            _removedCommands.Remove(text);
            _historyVersion++;
        }
        QueueHistoryFileJob(new HistoryFileJob
        {
            Append = text
        });
    }

    private void ProcessHistoryQueue()
    {
        // The file depends on the shell, so a command typed right after connecting waits for the probe here.
        string historyFile = GetHistoryFile();
        while (true)
        {
            string command;
            HistoryFileJob removal = null;
            lock (_historyQueue)
            {
                if (_historyQueue.Count == 0 || _disposed)
                {
                    _historyWriterBusy = false;
                    return;
                }
                if (_historyQueue.Peek().Remove != null)
                {
                    removal = _historyQueue.Dequeue();
                    command = CommandHistoryEditing.BuildRemoveCommand(historyFile, removal.Remove);
                }
                else
                {
                    // The lines queued before the next removal are written by one command: a channel per history line was slow.
                    // printf rather than echo: echo may interpret backslashes or take "-n"/"-e" at the start of a command as options.
                    List<string> lines = new List<string>();
                    while (_historyQueue.Count > 0 && _historyQueue.Peek().Remove == null)
                    {
                        lines.Add(_historyQueue.Dequeue().Append);
                    }
                    command = "printf '%s\\n' " + string.Join(" ", lines.Select(SmarTTY.IO.RemotePathUtils.ShellQuote).ToArray()) + " >> " + historyFile;
                }
                _historyWriterBusy = true;
            }
            // RunCommand rather than a channel left running in the background: a helper command must not overlap
            // with the PTY channel of the command being started (see Ssh/PtySetupGate.cs), otherwise dropbear on
            // OpenWRT dropped that command's exec request and the Smart Terminal tab hung.
            bool changed = true;
            try
            {
                if (command != null)
                {
                    _sshSession.RunCommand(command, out int? exitStatus);
                    changed = exitStatus == 0;
                }
            }
            catch
            {
                changed = false;
            }
            removal?.Done?.Invoke(changed);
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
