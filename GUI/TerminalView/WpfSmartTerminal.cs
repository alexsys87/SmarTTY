using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.Controls;
using SmarTTY.GUI.Backend;
using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Ssh;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The Smart Terminal tab on the WPF terminal (stage 2C): what <c>SshTerminalControl</c> did, without WinForms. At its prompt the terminal
/// edits the command line itself (<see cref="PromptLine"/>: typing, the caret and the selection, Ctrl+Left/Right, Home/End, Ctrl+U/K/L/C/D,
/// the history with Up/Down and Ctrl+R, the grey suggestion from the history, the completion list); Enter runs the command in an SSH
/// channel of its own (cd, export, unset, exit, clear and history are done here), and while it runs the keys go to it as in a classic
/// terminal. The logic is that of the WinForms control, moved as it was where it did not touch the editor; what the editor did is
/// <see cref="PromptLine"/> and the view.
/// </summary>
public sealed class WpfSmartTerminal : WpfTerminal, IFileListHost, IOutputLinkHost, ISmartTerminalHost, ICommandRunHost
{
    // The most output of a command that is received and not yet drawn (see SshTerminalControl.MaxUnprocessedOutputBytes).
    internal const int MaxUnprocessedOutputBytes = 4 * 1024 * 1024;

    private sealed class CommandChannel
    {
        private readonly SshShellChannel _channel;

        private readonly WpfSmartTerminal _terminal;

        public readonly CommandRun Run;

        private readonly Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

        private bool _outputStarted;

        public CommandChannel(SshShellChannel sshShellChannel, WpfSmartTerminal terminal, CommandRun commandRun)
        {
            _channel = sshShellChannel;
            _terminal = terminal;
            Run = commandRun;
            // Past this much output that is not yet drawn the channel stops reading, and the server is slowed down.
            sshShellChannel.LimitUnprocessedData(MaxUnprocessedOutputBytes);
            sshShellChannel.DataReceived += OnDataReceived;
        }

        private void OnDataReceived(object sender, byte[] buffer, int offset, int count, int flags)
        {
            bool handedOver = false;
            try
            {
                // In byte mode the terminal needs to see which stray byte arrived (see Utf8StreamDecoder).
                _decoder.EscapeInvalidBytes = _terminal.ByteOrientedLineEditing;
                string text = _decoder.Decode(buffer, offset, count);
                if (!_outputStarted)
                {
                    text = text.TrimStart('\r', '\n');
                    if (text == "")
                    {
                        return;
                    }
                    _outputStarted = true;
                }
                handedOver = true;
                _terminal.PostToUi(() =>
                {
                    try
                    {
                        if (_terminal._channel == this)
                        {
                            _terminal.ProcessOutput(text);
                        }
                    }
                    finally
                    {
                        // Only now is the chunk off the books: the channel reads on once the backlog is small enough.
                        _channel.MarkProcessed(count);
                    }
                });
            }
            finally
            {
                if (!handedOver)
                {
                    _channel.MarkProcessed(count);
                }
            }
        }

        public void SendInput(byte[] buffer)
        {
            try
            {
                _channel.Input.Write(buffer, 0, buffer.Length);
            }
            catch
            {
                // The command has just exited (or the connection is gone): the prompt is about to come back.
            }
        }

        public void Resize(int columns, int rows)
        {
            try
            {
                _channel.ResizePty(columns, rows);
            }
            catch
            {
            }
        }

        // Closing a channel may wait for the server, so it is never done on the UI thread.
        public void DisposeInBackground()
        {
            SshShellChannel channel = _channel;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                }
            });
        }
    }

    private enum CompletionTrigger
    {
        Normal,
        Auto,
        Options,
        Tab
    }

    // "Paste line by line": the commands that are still to be run, one after another.
    private sealed class LinePaste
    {
        public readonly LinePasteRun Run;

        public IPasteProgress Progress;

        public LinePaste(List<string> commands, bool stopOnError)
        {
            Run = new LinePasteRun(commands, stopOnError);
        }
    }

    private readonly ITerminalSessionContext _context;

    private readonly string _homeDirectory;

    private readonly HashSet<string> _linkExtensions;

    private bool _welcomeShown;

    // The command line at the prompt; null while a command runs.
    private PromptLine _prompt;

    // Up/Down of the command line (a new walk for every prompt, as the WinForms control made a new InputLine).
    private HistoryWalk _walk = new HistoryWalk();

    private CommandChannel _channel;

    private LinePaste _linePaste;

    // The command that ran last ended with an error (a non-zero exit code, a lost connection, a failed cd).
    private bool _lastCommandFailed;

    private string _currentDirectory;

    private string _previousDirectory;

    private CompletionSession _completion;

    private bool _applyingCompletion;

    // Ctrl+R: the bash-like incremental history search in progress, and the line typed before it started.
    private HistorySearch _historySearch;

    private string _searchOriginalText;

    // The grey rest of a previously typed command drawn after the caret, and what hides it.
    private readonly HistorySuggestionState _suggestion = new HistorySuggestionState();

    private readonly SuggestionLookup _suggestionLookup = new SuggestionLookup();

    private readonly List<string> _typedCommands = new List<string>();

    // Variables set with "export" (a null value: "unset"), applied in front of every command.
    private readonly SessionEnvironment _environment = new SessionEnvironment();

    private readonly List<CommandRun> _runs = new List<CommandRun>();

    private readonly Queue<Action> _uiQueue = new Queue<Action>();

    private ICompletionPopup _popup;

    // A pass of the queue of the UI stops after this long and lets the window run (draw, a typed Ctrl+C) before it goes on.
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(25);

    public WpfSmartTerminal(ITerminalSessionContext terminalSessionContext, string initialDirectory = null)
    {
        _context = terminalSessionContext;
        _homeDirectory = (_currentDirectory = terminalSessionContext.Session.RunCommandChecked("pwd", requireSuccess: true)?.Where((string text) => text != "")?.LastOrDefault());
        if (initialDirectory != null)
        {
            _currentDirectory = initialDirectory;
        }
        _linkExtensions = VirtualTerminalSettings.ParseAutoLinkExtensions(terminalSessionContext.VTSettings.AutoLinkExtensions);
        if (_homeDirectory == null)
        {
            throw new RemoteOperationException("Failed to query home directory. Please try disabling virtual mode.");
        }
        // The links of the output are those of the command that runs, and open with a plain click (a classic terminal needs Ctrl).
        Model.LinkScannerFactory = CreateCommandLinkScanner;
        View.LinksNeedControl = false;
        UpdateTitle();
        DataEntered += (WpfTerminal sender, byte[] buffer, TerminalInputKind kind) => _channel?.SendInput(buffer);
        TerminalSizeChanged += (WpfTerminal sender, int columns, int rows) => _channel?.Resize(columns, rows);
        // Starts loading the history and the list of executables in the background.
        terminalSessionContext.ProvideAnalyzer();
    }

    public event Action<string> DirectoryChanged;

    public event EventHandler<OpenFileEventArgs> OpenFileRequested;

    public event EventHandler EndOfInputRequested;

    public event EventHandler<CompletionPopupRequest> CompletionPopupRequested;

    // Asked from the session every time: a reconnect replaces the analyzer (and the SSH session it uses).
    internal ShellCompletionAnalyzer ShellAnalyzer => _context.ProvideAnalyzer();

    // "/home/user2" is not under the home directory "/home/user": the prefix must end at a path boundary.
    private string PromptPrefix => _context.TitleWithUserName + ":" + ShellCommandLine.AbbreviateHome(_currentDirectory, _homeDirectory);

    public string CurrentDirectory => _currentDirectory;

    public SshSession Session => _context.Session;

    public bool HasEnvironment => _environment.Any;

    /// <summary>Variables exported in this tab (for completing $NAME).</summary>
    public IEnumerable<string> EnvironmentNames => _environment.Names;

    public string HomeDirectory => _homeDirectory;

    public VirtualTerminalSettings VtSettings => _context.VTSettings;

    /// <summary>The terminal waits at its prompt (no command runs).</summary>
    public bool IsAtPrompt => _prompt != null;

    /// <summary>At the prompt the command line is edited here: the window leaves Ctrl+C, Ctrl+X and Ctrl+V to it.</summary>
    public override bool EditsLocally => _prompt != null;

    // The history.

    // Newest first, without duplicates: the history file of the session followed by what was typed in this tab.
    private List<string> BuildHistory()
    {
        string[] items = ShellAnalyzer?.GetHistory() ?? new string[0];
        List<string> list = new List<string>();
        HashSet<string> seen = new HashSet<string>();
        foreach (string item in items.Concat(_typedCommands).Reverse())
        {
            if (seen.Add(item))
            {
                list.Add(item);
            }
        }
        return list;
    }

    /// <summary>The history of this tab for the "Command history" window: every command once, the newest first.</summary>
    public List<string> GetCommandHistory()
    {
        return CommandHistoryEditing.Distinct(BuildHistory());
    }

    /// <summary>Forgets <paramref name="commands"/> in this tab after they were removed from the history of the session.</summary>
    public void ForgetCommands(IEnumerable<string> commands)
    {
        HashSet<string> set = new HashSet<string>(commands ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return;
        }
        _typedCommands.RemoveAll(set.Contains);
        _walk.Reset();
        _suggestionLookup.Invalidate();
        RefreshHistorySuggestion();
    }

    /// <summary>
    /// Puts a command from the history window into the command line in place of what is typed there, ready to be run with Enter or edited.
    /// While a program runs the text is typed into it, as a quick command.
    /// </summary>
    public void PutCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }
        if (_prompt == null)
        {
            base.InsertCommand(command);
            return;
        }
        if (_historySearch != null)
        {
            EndHistorySearch(accept: false);
        }
        CloseCompletion();
        PrepareInputForEditing();
        _prompt.SetText(command);
    }

    // Links (the host of the scanners of the output and of the completion).

    public void RequestOpenUrl(string url)
    {
        UrlOpener.Open(url);
    }

    public void RequestOpenFile(string text, int? line, int? column, ColumnUnit unit)
    {
        OpenFileRequested?.Invoke(this, new OpenFileEventArgs
        {
            Path = text,
            Column = column,
            Line = line,
            ColumnUnit = unit
        });
    }

    public bool IsLinkExtension(string text)
    {
        return _linkExtensions?.Contains(text) ?? false;
    }

    ShellCompletionAnalyzer ISmartTerminalHost.ShellAnalyzer => ShellAnalyzer;

    bool ISmartTerminalHost.TryBeginInvoke(Action action)
    {
        if (IsDisposed)
        {
            return false;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        return true;
    }

    ColumnUnit ICommandRunHost.CompilerColumns => VtSettings?.CompilerColumns ?? ColumnUnit.Auto;

    private ILinkScanner CreateCommandLinkScanner()
    {
        // No extensions means no file links; the words of errors and warnings are marked on their own setting. Addresses are links in any
        // case (they open in the browser).
        bool links = !string.IsNullOrEmpty(VtSettings.AutoLinkExtensions);
        bool highlight = VtSettings.HighlightDiagnostics;
        return _channel?.Run?.CreateScanner(links, highlight);
    }

    // The directory.

    private void UpdateTitle()
    {
        Model.SetTitle(PromptPrefix);
    }

    /// <summary>
    /// Changes the directory of the tab to a literal path (absolute or relative to the current directory), as the file list does. The line
    /// being typed is kept; while a command is running only the next prompt is affected. Throws if the directory cannot be entered.
    /// </summary>
    public void ChangeDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        string target = RemotePathUtils.NormalizeUnix(RemotePathUtils.Combine(_currentDirectory, path));
        string trimmedPath = target.TrimEnd('/');
        if (trimmedPath.Length > 0)
        {
            target = trimmedPath;
        }
        string command = "cd " + RemotePathUtils.ShellQuote(target);
        List<string> output = _context.Session.RunCommand(command, out int? exitCode);
        if (!exitCode.HasValue && (output == null || output.Count == 0) && !_context.Session.IsConnectionClosed)
        {
            // The channel was closed without an exit status: "cd" is safe to repeat.
            output = _context.Session.RunCommand(command, out exitCode);
        }
        if (exitCode != 0)
        {
            string message = string.Join(Environment.NewLine, (output ?? new List<string>()).Where((string line) => line.Trim() != ""));
            if (message == "")
            {
                message = exitCode.HasValue
                    ? $"Cannot change the directory to {target} (exit code {exitCode.Value})"
                    : $"Cannot change the directory to {target} (the server did not report an exit code)";
            }
            throw new RemoteOperationException(message);
        }
        SetCurrentDirectory(target);
        if (_prompt != null)
        {
            RefreshPrompt();
        }
    }

    private void SetCurrentDirectory(string directory)
    {
        if (directory == _currentDirectory)
        {
            return;
        }
        _previousDirectory = _currentDirectory;
        _currentDirectory = directory;
        UpdateTitle();
        DirectoryChanged?.Invoke(directory);
    }

    /// <summary>
    /// Runs a typed "cd" command. The remote shell resolves the argument (~, $VAR, CDPATH, globs, quotes) and reports the resulting
    /// directory. "cd -" goes back to the previous directory.
    /// </summary>
    private void RunChangeDirectoryCommand(string arguments)
    {
        arguments = (arguments ?? "").Trim();
        string resolvedArguments = arguments;
        if (arguments == "-")
        {
            if (_previousDirectory == null)
            {
                _lastCommandFailed = true;
                PrintLines(new[] { AnsiEscapeUtils.Colorize("cd: OLDPWD not set", 1) });
                return;
            }
            resolvedArguments = RemotePathUtils.ShellQuote(_previousDirectory);
        }
        string remoteCommand = BuildRemoteCommand("cd " + resolvedArguments + " >/dev/null && pwd", interactive: false);
        List<string> output = _context.Session.RunCommand(remoteCommand, out int? exitCode);
        if (!exitCode.HasValue && (output == null || output.Count == 0) && !_context.Session.IsConnectionClosed)
        {
            // Some servers (Dropbear) now and then close the exec channel without an exit status and without output: ask once more.
            output = _context.Session.RunCommand(remoteCommand, out exitCode);
        }
        string newDirectory = (exitCode == 0) ? output?.LastOrDefault((string line) => line.StartsWith("/")) : null;
        if (newDirectory == null)
        {
            List<string> errors = (output ?? new List<string>()).Where((string line) => line.Trim() != "").ToList();
            if (errors.Count == 0)
            {
                errors.Add(exitCode.HasValue
                    ? $"'cd {arguments}' exited with code {exitCode.Value}"
                    : $"'cd {arguments}' did not report an exit code");
            }
            _lastCommandFailed = true;
            PrintLines(errors.Select((string line) => AnsiEscapeUtils.Colorize(line, 1)));
            return;
        }
        if (arguments == "-")
        {
            PrintLines(new[] { newDirectory });
        }
        SetCurrentDirectory(newDirectory);
    }

    private void PrintLines(IEnumerable<string> lines)
    {
        StringBuilder output = new StringBuilder();
        foreach (string line in lines)
        {
            output.Append(line).Append("\r\n");
        }
        if (output.Length > 0)
        {
            ProcessOutput(output.ToString());
        }
    }

    // The prompt.

    public void Start()
    {
        if (!_welcomeShown)
        {
            ProcessOutput("Welcome to Smart Terminal.");
            _welcomeShown = true;
            ShowPrompt();
        }
    }

    private void ShowPrompt(bool removeTrailingBlankLine = true, string customPrompt = null)
    {
        TerminalDisplayModel model = Model;
        model.SwitchToNormalScreen();
        EditableTextDocument document = model.Document;
        if (removeTrailingBlankLine && document.LineCount > 0 && document.GetLineText(document.LineCount - 1).Trim() == "")
        {
            model.ReplaceDocumentTail(document.LineCount - 1, new string[0]);
        }
        char ch = _context.IsRoot ? '#' : '$';
        _suggestion.Clear();
        _prompt = new PromptLine(model, document.LineCount, customPrompt ?? $"{PromptPrefix}{ch} ");
        _prompt.TextChanged += OnPromptTextChanged;
        _prompt.CaretMoved += RefreshHistorySuggestion;
        _walk = new HistoryWalk();
        UpdateGhostText();
        if (_linePaste != null && !IsDisposed)
        {
            // The next pasted command, once this prompt is on the screen (not from inside the command that has just ended).
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RunNextPastedLine));
        }
    }

    // Redraws the prompt (after the directory has changed) without losing what has been typed.
    private void RefreshPrompt()
    {
        if (_prompt == null)
        {
            return;
        }
        if (_historySearch != null)
        {
            EndHistorySearch(accept: true);
        }
        ReplacePromptLine(null, _prompt.Text, _prompt.CaretIndex);
    }

    /// <summary>Replaces the prompt line (with a custom prompt, or the normal one when <paramref name="prompt"/> is null) and puts <paramref name="text"/> on it.</summary>
    private void ReplacePromptLine(string prompt, string text, int caretIndex)
    {
        PromptLine line = _prompt;
        if (line == null)
        {
            return;
        }
        text = text ?? "";
        CloseCompletion();
        Model.ReplaceDocumentTail(line.StartLine, new string[0]);
        _prompt = null;
        ShowPrompt(removeTrailingBlankLine: false, customPrompt: prompt);
        _prompt.SetText(text, (caretIndex >= 0 && caretIndex <= text.Length) ? caretIndex : text.Length);
    }

    private void OnPromptTextChanged(int start, int removed, int added)
    {
        string filter = _prompt?.Text;
        _popup?.SetFilter(filter);
        CompletionSession session = _completion;
        if (session != null && !_applyingCompletion && session.OnTextChanged(filter, start, removed, added))
        {
            _completion.Dispose();
            _completion = null;
        }
        RefreshHistorySuggestion();
    }

    protected override void OnGridResized(int columns, int rows)
    {
        // The command line is wrapped at the new width (the caret keeps its index).
        _prompt?.Render();
        UpdateGhostText();
    }

    protected override void OnDocumentClicked(TextDocument.TextPosition position)
    {
        if (_prompt != null && _prompt.Contains(position))
        {
            _prompt.MoveCaret(_prompt.IndexOf(position), extend: false);
        }
    }

    /// <summary>Typing goes to the command line even when text above it is selected (e.g. to copy it).</summary>
    private void PrepareInputForEditing()
    {
        if (_prompt == null)
        {
            return;
        }
        TerminalDisplayModel model = Model;
        if (model.HasSelection && !_prompt.HasSelection)
        {
            model.ClearSelection();
        }
    }

    // The history search (Ctrl+R).

    private void StartHistorySearch()
    {
        if (_prompt == null)
        {
            return;
        }
        _searchOriginalText = _prompt.Text ?? "";
        _historySearch = new HistorySearch(BuildHistory());
        ShowHistorySearchLine();
    }

    // "(reverse-i-search)`query': match", as bash shows it.
    private void ShowHistorySearchLine()
    {
        HistorySearch search = _historySearch;
        if (search == null)
        {
            return;
        }
        string text = search.Match ?? _searchOriginalText;
        ReplacePromptLine(search.Prompt, text, search.MatchPosition >= 0 ? search.MatchPosition : text.Length);
    }

    /// <summary>Leaves the history search with the found command (or with the line typed before it when cancelled).</summary>
    private void EndHistorySearch(bool accept)
    {
        HistorySearch search = _historySearch;
        if (search == null)
        {
            return;
        }
        _historySearch = null;
        string text = (accept ? search.Match : null) ?? _searchOriginalText;
        int caretIndex = (accept && search.Match != null && search.MatchPosition >= 0) ? search.MatchPosition : text.Length;
        ReplacePromptLine(null, text, caretIndex);
    }

    // Keys while the history search is active. Returns true for a key it has handled completely.
    private bool HandleHistorySearchKey(VirtualKey key, bool control, bool alt)
    {
        HistorySearch search = _historySearch;
        switch (key)
        {
            case VirtualKey.R when control && !alt:
                search.Older();
                ShowHistorySearchLine();
                return true;
            case VirtualKey.S when control && !alt:
                search.Newer();
                ShowHistorySearchLine();
                return true;
            case VirtualKey.G when control && !alt:
            case VirtualKey.Escape:
                EndHistorySearch(accept: false);
                return true;
            case VirtualKey.C when control && !alt:
                _searchOriginalText = "";
                EndHistorySearch(accept: false);
                return true;
            case VirtualKey.Back:
                search.RemoveLast();
                ShowHistorySearchLine();
                return true;
            case VirtualKey.Return:
                EndHistorySearch(accept: true);
                SubmitInput();
                return true;
        }
        return false;
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.ShiftKey:
            case VirtualKey.LShiftKey:
            case VirtualKey.RShiftKey:
            case VirtualKey.ControlKey:
            case VirtualKey.LControlKey:
            case VirtualKey.RControlKey:
            case VirtualKey.Menu:
            case VirtualKey.LMenu:
            case VirtualKey.RMenu:
            case VirtualKey.LWin:
            case VirtualKey.RWin:
            case VirtualKey.Capital:
                return true;
        }
        return false;
    }

    // Keys that leave the history search keeping the found command for editing, and then act as usual (as in bash).
    private static bool IsEditingKey(VirtualKey key, bool control, bool alt)
    {
        switch (key)
        {
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Tab:
            case VirtualKey.Delete:
            case VirtualKey.PageUp:
            case VirtualKey.PageDown:
            case VirtualKey.Insert:
                return true;
        }
        // Other Ctrl shortcuts as well, but not Ctrl+Alt (AltGr), which types characters on many layouts.
        return control && !alt;
    }

    // The grey suggestion.

    private List<string> GetSuggestionHistory()
    {
        ShellCompletionAnalyzer analyzer = ShellAnalyzer;
        int historyVersion = analyzer?.HistoryVersion ?? 0;
        return _suggestionLookup.GetHistory(analyzer, historyVersion, _typedCommands.Count, BuildHistory);
    }

    private string ComputeHistorySuggestion()
    {
        PromptLine line = _prompt;
        if (line == null || _historySearch != null || Model.HasSelection || !CompletionTriggers().HistorySuggestions)
        {
            return null;
        }
        string text = line.Text ?? "";
        if (text.Length == 0 || line.CaretIndex != text.Length || text == _suggestion.DismissedFor)
        {
            return null;
        }
        return _suggestionLookup.Find(GetSuggestionHistory(), text);
    }

    /// <summary>Works out the grey suggestion for the line as it is now and draws it when it has changed.</summary>
    private void RefreshHistorySuggestion()
    {
        string suggestion;
        try
        {
            suggestion = ComputeHistorySuggestion();
        }
        catch
        {
            suggestion = null;
        }
        _suggestion.Show(suggestion);
        UpdateGhostText();
    }

    private void UpdateGhostText()
    {
        PromptLine line = _prompt;
        string suggestion = _suggestion.Current;
        if (line == null || suggestion == null || _historySearch != null || Model.HasSelection)
        {
            View.SetGhostText(default(TextDocument.TextPosition), null);
            return;
        }
        // The command line is the end of the document, so the suggestion starts where it ends.
        View.SetGhostText(line.PositionAt(line.Text.Length), suggestion);
    }

    /// <summary>
    /// Right or End at the end of the line take the whole suggestion, Ctrl+Right its next word (as in fish). Returns false, leaving the key
    /// to its usual meaning, when no suggestion is shown.
    /// </summary>
    private bool TryAcceptHistorySuggestion(VirtualKey key, bool shift, bool control, bool alt)
    {
        PromptLine line = _prompt;
        if (line == null || shift || alt)
        {
            return false;
        }
        bool wholeCommand = !control && (key == VirtualKey.Right || key == VirtualKey.End);
        bool nextWord = control && key == VirtualKey.Right;
        if (!wholeCommand && !nextWord)
        {
            return false;
        }
        RefreshHistorySuggestion();
        string suggestion = _suggestion.Current;
        if (suggestion == null)
        {
            return false;
        }
        string part = wholeCommand ? suggestion : HistorySuggestion.NextWord(suggestion);
        if (part.Length == 0)
        {
            return false;
        }
        // The list was made for the word being typed, which the suggestion has just changed.
        CloseCompletion();
        line.SetText((line.Text ?? "") + part);
        return true;
    }

    /// <summary>Esc hides the suggestion until the line is changed.</summary>
    private void DismissHistorySuggestion()
    {
        if (_suggestion.Current == null)
        {
            return;
        }
        _suggestion.Dismiss(_prompt?.Text);
        RefreshHistorySuggestion();
    }

    // The completion list.

    private AutoCompletionTriggers CompletionTriggers()
    {
        return _context?.VTSettings?.CompletionTriggers ?? new AutoCompletionTriggers();
    }

    private bool AtWordStartTriggerEnabled()
    {
        return CompletionTriggers().FirstCharacterInWord;
    }

    private void ShowCompletions(CompletionTrigger completionTrigger = CompletionTrigger.Normal)
    {
        ICompletionPopup completionPopup = _popup;
        if (completionPopup != null && completionPopup.IsOpen)
        {
            return;
        }
        CompletionSession completionSession = _completion;
        if (completionSession != null && completionSession.IsOpen)
        {
            return;
        }
        PromptLine line = _prompt;
        if (line == null)
        {
            return;
        }
        string text = line.Text ?? "";
        int caretIndex = line.CaretIndex;
        if (caretIndex < 0 || caretIndex > text.Length)
        {
            caretIndex = text.Length;
        }
        // The word under the caret is replaced; when the caret is between words, an empty word at the caret is completed.
        ShellCommandLine.CompletionContext context = ShellCommandLine.Analyze(text, caretIndex);
        // Options are looked up for the command the caret is in ("sudo apt inst", "ls | grep -"), not the first word.
        CommandCompletion commandCompletion = ShellAnalyzer.CompletionDatabase?.LookupCommand(text.Substring(context.CommandStart));
        switch (completionTrigger)
        {
            case CompletionTrigger.Options:
                if (commandCompletion?.Options == null)
                {
                    return;
                }
                break;
            case CompletionTrigger.Auto:
                if (commandCompletion != null && commandCompletion.SuppressAutoTriggers)
                {
                    return;
                }
                break;
        }
        CompletionTarget completionTarget = context.Target;
        CompletionSession.Token token = new CompletionSession.Token(text, context.TokenStart, context.TokenEnd);
        // A session whose popup was closed from outside (a click elsewhere) must not apply its late results.
        completionSession?.Dispose();
        _completion = null;
        GetCaretScreenPlace(out GridPoint location, out int lineHeight);
        CompletionPopupRequest completionPopupRequest = new CompletionPopupRequest
        {
            ScreenLocation = location,
            LineHeight = lineHeight,
            Items = null,
            StatusText = "Loading suggestions..."
        };
        CompletionPopupRequested?.Invoke(this, completionPopupRequest);
        if (completionPopupRequest.Popup != null)
        {
            _completion = new CompletionSession(this, text, completionTarget, token, completionPopupRequest.Popup, completionTrigger == CompletionTrigger.Tab, commandCompletion, context.InsertPrefix, context.InsertSuffix);
            _completion.ItemApplied += OnCompletionApplied;
        }
    }

    private void OnCompletionApplied(object sender, CompletionSession.AppliedEventArgs item)
    {
        PromptLine line = _prompt;
        if (line != null)
        {
            // The session updates itself; reporting this replacement to it as typing used to end it.
            _applyingCompletion = true;
            try
            {
                line.SetText(item.NewText, item.CaretIndex);
            }
            finally
            {
                _applyingCompletion = false;
            }
        }
        if (item.Commit)
        {
            SubmitInput();
        }
    }

    private void ShowHistoryPopup()
    {
        CloseCompletion();
        GetCaretScreenPlace(out GridPoint location, out int lineHeight);
        CompletionPopupRequest completionPopupRequest = new CompletionPopupRequest
        {
            ScreenLocation = location,
            LineHeight = lineHeight,
            Items = BuildHistory()?.Select((string historyCommand) => new CompletionItem
            {
                Icon = VirtualTerminalPopupItemIcon.Command,
                Text = historyCommand
            })?.ToArray(),
            Title = "Command history",
            Icon = VirtualTerminalPopupIcon.History
        };
        CompletionPopupRequested?.Invoke(this, completionPopupRequest);
        ICompletionPopup popup = completionPopupRequest.Popup;
        _popup = popup;
        if (popup != null)
        {
            popup.ItemSelected += (CompletionItem completionItem, VirtualKey keys) =>
            {
                if (_popup == popup)
                {
                    _popup = null;
                }
                if (_prompt == null)
                {
                    return;
                }
                _prompt.SetText(completionItem.Text);
                if (keys == VirtualKey.Return)
                {
                    SubmitInput();
                }
            };
            popup.SetFilter(_prompt?.Text);
        }
    }

    private void CloseCompletion()
    {
        _popup?.Close();
        _popup = null;
        _completion?.Dispose();
        _completion = null;
    }

    // The keys at the prompt (OnKeyDown and OnKeyPress of SshTerminalControl, and the editing keys of its editor). While a command runs
    // the keys are left to the terminal, which sends them to the command.

    protected override void OnTerminalKey(KeyEventArgs e)
    {
        PromptLine line = _prompt;
        if (line == null)
        {
            return;
        }
        Key wpfKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (wpfKey == Key.ImeProcessed || wpfKey == Key.DeadCharProcessed)
        {
            // The character comes as text input.
            return;
        }
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        VirtualKey key = (VirtualKey)KeyInterop.VirtualKeyFromKey(wpfKey);
        KeyStroke stroke = new KeyStroke(key | (shift ? VirtualKey.Shift : 0) | (control ? VirtualKey.Control : 0) | (alt ? VirtualKey.Alt : 0));

        // A key that ends the history search keeps the found command and moves the caret: it must not also take a suggestion.
        bool wasSearching = _historySearch != null;
        if (_historySearch != null)
        {
            if (IsModifierKey(key))
            {
                return;
            }
            if (HandleHistorySearchKey(key, control, alt))
            {
                e.Handled = true;
                return;
            }
            if (!IsEditingKey(key, control, alt))
            {
                // A character key: the text input adds it to the query.
                return;
            }
            EndHistorySearch(accept: true);
            line = _prompt;
        }
        if (IsModifierKey(key))
        {
            return;
        }
        // Before the completion list: it leaves Right and End alone, so the suggestion can be taken while it is open.
        if (!wasSearching && TryAcceptHistorySuggestion(key, shift, control, alt))
        {
            e.Handled = true;
            return;
        }
        CompletionSession completionSession = _completion;
        ICompletionPopup completionPopup = _popup;
        CompletionSession.AppliedEventArgs appliedBefore = completionSession?.LastApplied;
        if ((completionPopup != null && completionPopup.TryHandleKeyEvent(stroke)) || (completionSession != null && completionSession.HandleKey(stroke)))
        {
            e.Handled = true;
            // Only an item applied by this very key counts: LastApplied stays set after Tab auto-completed a directory.
            CompletionSession.AppliedEventArgs applied = completionSession?.LastApplied;
            if (applied != null && applied != appliedBefore && applied.IsDirectory && key != VirtualKey.Return && _completion == completionSession && _prompt != null)
            {
                // A directory was chosen with Tab: list its contents right away.
                completionSession.Dispose();
                _completion = null;
                ShowCompletions();
            }
            return;
        }
        line = _prompt;
        if (line == null)
        {
            return;
        }
        if (key == VirtualKey.Escape)
        {
            // The first Esc closes the completion list, the next one hides the suggestion.
            bool listOpen = (_popup != null && _popup.IsOpen) || (_completion != null && _completion.IsOpen);
            CloseCompletion();
            if (!listOpen)
            {
                DismissHistorySuggestion();
            }
            e.Handled = true;
            return;
        }
        if (control && !alt)
        {
            switch (key)
            {
                case VirtualKey.L:
                    {
                        string text = line.Text;
                        ClearScreen();
                        _prompt?.SetText(text);
                        e.Handled = true;
                        return;
                    }
                case VirtualKey.C when !shift:
                    if (Model.HasSelection)
                    {
                        CopySelection();
                    }
                    else
                    {
                        CloseCompletion();
                        line.SetText("");
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.X when !shift:
                    if (line.HasSelection)
                    {
                        CopySelection();
                        PrepareInputForEditing();
                        line.DeleteSelection();
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.V when !shift:
                    Paste();
                    e.Handled = true;
                    return;
                case VirtualKey.A:
                    line.SelectAll();
                    UpdateGhostText();
                    e.Handled = true;
                    return;
                case VirtualKey.D:
                    // As in bash, Ctrl+D closes the tab only on an empty line.
                    if (string.IsNullOrEmpty(line.Text))
                    {
                        RequestEndOfInput();
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.U:
                    DeleteInput(toStart: true);
                    e.Handled = true;
                    return;
                case VirtualKey.K:
                    DeleteInput(toStart: false);
                    e.Handled = true;
                    return;
                case VirtualKey.R:
                    // Ctrl+R searches the history as bash does; Ctrl+Shift+R shows it as a list.
                    if (shift)
                    {
                        ShowHistoryPopup();
                    }
                    else
                    {
                        StartHistorySearch();
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.Space:
                    if (CompletionTriggers().CtrlSpace)
                    {
                        ShowCompletions();
                    }
                    e.Handled = true;
                    return;
            }
        }
        if (alt)
        {
            return;
        }
        switch (key)
        {
            case VirtualKey.Up:
            case VirtualKey.Down:
                if (!shift && !control)
                {
                    NavigateHistory(key == VirtualKey.Up, CompletionTriggers().HistoryPrefixSearch);
                    e.Handled = true;
                }
                return;
            case VirtualKey.Left:
            case VirtualKey.Right:
                line.MoveCaret(line.StepIndex(key == VirtualKey.Right, control), shift);
                UpdateGhostText();
                e.Handled = true;
                return;
            case VirtualKey.Home:
                line.MoveCaret(0, shift);
                e.Handled = true;
                return;
            case VirtualKey.End:
                line.MoveCaret(line.Text.Length, shift);
                e.Handled = true;
                return;
            case VirtualKey.Back:
                PrepareInputForEditing();
                line.DeleteBackward(control);
                e.Handled = true;
                return;
            case VirtualKey.Delete:
                if (shift)
                {
                    // Shift+Delete cuts, as in the editor.
                    if (line.HasSelection)
                    {
                        CopySelection();
                        line.DeleteSelection();
                    }
                }
                else
                {
                    PrepareInputForEditing();
                    line.DeleteForward(control);
                }
                e.Handled = true;
                return;
            case VirtualKey.Return:
                SubmitInput();
                e.Handled = true;
                return;
            case VirtualKey.Tab:
                if (!control)
                {
                    CompletionSession activeCompletion = _completion;
                    if (activeCompletion == null || !activeCompletion.IsOpen)
                    {
                        ShowCompletions(CompletionTrigger.Tab);
                    }
                    else
                    {
                        // The list is open (e.g. after a space) but Tab did not pick an item from it.
                        activeCompletion.ApplyOnTab();
                    }
                    e.Handled = true;
                }
                return;
            case VirtualKey.PageUp:
            case VirtualKey.PageDown:
                if (!shift)
                {
                    Model.TopLine += key == VirtualKey.PageUp ? -Model.Rows : Model.Rows;
                    e.Handled = true;
                }
                return;
        }
        if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
        {
            // Nothing runs to get them.
            e.Handled = true;
        }
    }

    private void NavigateHistory(bool older, bool byPrefix)
    {
        PromptLine line = _prompt;
        if (line == null)
        {
            return;
        }
        string shown = _walk.Step(line.Text, older, byPrefix, BuildHistory);
        if (shown == null)
        {
            return;
        }
        try
        {
            line.SetText(shown);
        }
        catch
        {
        }
    }

    // Ctrl+U / Ctrl+K: delete from the caret to the start / end of the command line, as in readline.
    private void DeleteInput(bool toStart)
    {
        PromptLine line = _prompt;
        if (line == null)
        {
            return;
        }
        PrepareInputForEditing();
        string text = line.Text ?? "";
        int caretIndex = line.CaretIndex;
        CloseCompletion();
        if (toStart)
        {
            line.SetText(text.Substring(caretIndex), 0);
        }
        else
        {
            line.SetText(text.Substring(0, caretIndex), caretIndex);
        }
    }

    protected override bool OnTerminalText(TextCompositionEventArgs e)
    {
        PromptLine line = _prompt;
        if (line == null)
        {
            // A command runs: the terminal sends the text to it.
            return false;
        }
        string text = e.Text;
        if (string.IsNullOrEmpty(text))
        {
            // Alt with a character (system text) and Ctrl with a key the line does not use: nothing at the prompt.
            return true;
        }
        if (_historySearch != null)
        {
            // Typed characters extend the search query instead of the command line.
            string query = new string(text.Where(ch => ch >= ' ').ToArray());
            if (query.Length != 0)
            {
                _historySearch.Append(query);
                ShowHistorySearchLine();
            }
            return true;
        }
        if (text.Length == 1)
        {
            TypeCharacter(text[0]);
            return true;
        }
        // More than one character at once (the result of an input method): typed as one piece.
        string clean = new string(text.Where(ch => ch >= ' ').ToArray());
        if (clean.Length != 0)
        {
            PrepareInputForEditing();
            line.Insert(clean);
        }
        return true;
    }

    private void TypeCharacter(char ch)
    {
        PromptLine line = _prompt;
        switch (ch)
        {
            case '\r':
                SubmitInput();
                return;
            case '\t':
                ShowCompletions(CompletionTrigger.Tab);
                return;
            case '\b':
                PrepareInputForEditing();
                line.DeleteBackward(word: false);
                return;
        }
        if (ch < ' ')
        {
            return;
        }
        PrepareInputForEditing();
        bool? isAtWordStart = null;
        if (ch > ' ' && AtWordStartTriggerEnabled())
        {
            isAtWordStart = line.IsAtWordStart;
        }
        CompletionSession activeCompletion = _completion;
        if (activeCompletion != null && activeCompletion.IsOpen && activeCompletion.HandleChar(ch))
        {
            activeCompletion.Dispose();
            _completion = null;
            switch (ch)
            {
                case ' ':
                    if (CompletionTriggers().Space)
                    {
                        ShowCompletions(CompletionTrigger.Auto);
                    }
                    break;
                case '/':
                    if (CompletionTriggers().Slash)
                    {
                        ShowCompletions();
                    }
                    break;
            }
            return;
        }
        line.Insert(ch.ToString());
        switch (ch)
        {
            case ' ':
                if (CompletionTriggers().Space)
                {
                    ShowCompletions(CompletionTrigger.Auto);
                }
                break;
            case '/':
                if (CompletionTriggers().Slash)
                {
                    ShowCompletions();
                }
                break;
            default:
                if (AtWordStartTriggerEnabled() && isAtWordStart == true)
                {
                    ShowCompletions((ch != '-') ? CompletionTrigger.Auto : CompletionTrigger.Options);
                }
                break;
        }
    }

    // Clear, the end of the input.

    /// <summary>"Clear" of the context menu: the same as Ctrl+L. While a command runs nothing is cleared.</summary>
    public override void ClearOutput()
    {
        if (_prompt == null)
        {
            return;
        }
        string text = _prompt.Text;
        ClearScreen();
        _prompt?.SetText(text);
    }

    // Ctrl+L and "clear": an empty screen with the prompt at the top.
    private void ClearScreen()
    {
        _historySearch = null;
        CloseCompletion();
        _prompt = null;
        Reset();
        ShowPrompt();
    }

    // Closing the tab disposes this terminal, so it is done after the key handler has returned.
    private void RequestEndOfInput()
    {
        if (EndOfInputRequested == null || IsDisposed)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!IsDisposed)
            {
                EndOfInputRequested?.Invoke(this, EventArgs.Empty);
            }
            // The tab stayed open ("exit" was typed, closing was cancelled): give the prompt back.
            if (!IsDisposed && _prompt == null && _channel == null)
            {
                ShowPrompt();
            }
        }));
    }

    // Running commands.

    private void BeginExecution()
    {
        _historySearch = null;
        _prompt = null;
        _suggestion.Clear();
        UpdateGhostText();
        Model.BeginOutputAtEnd();
        CloseCompletion();
    }

    public string BuildRemoteCommand(string command, bool? interactive = null)
    {
        // Quoted as a single word: a directory name with a quote, $ or ` used to break every command.
        string text = "cd " + RemotePathUtils.ShellQuote(_currentDirectory) + " && ";
        interactive = interactive ?? _context.VTSettings.InteractiveModeWrapper;
        command = _environment.BuildExports() + command;
        if (interactive.Value)
        {
            // bash, or ash on OpenWrt, unless a shell is set by hand; until the shell of the host is known the command runs without it.
            ShellSelection.Choice shell = _context.ProvideAnalyzer().GetInteractiveShell();
            if (shell != null)
            {
                command = ShellSelection.WrapInteractive(shell, command);
            }
        }
        return text + command;
    }

    public bool SubmitInput()
    {
        return Submit(null);
    }

    /// <summary>Runs a command as if it had been typed (or, with null, what is typed): false when a command runs already.</summary>
    public bool Submit(string command)
    {
        if (_prompt == null)
        {
            return false;
        }
        if (command != null)
        {
            _prompt.SetText(command);
        }
        else
        {
            command = _prompt.Text;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowPrompt();
            return true;
        }
        _lastCommandFailed = false;
        BeginExecution();
        try
        {
            // As with HISTCONTROL=ignorespace, a command typed with a leading space is not saved in the history file.
            if (!command.StartsWith(" ", StringComparison.Ordinal))
            {
                _typedCommands.Add(command);
                ShellAnalyzer.AppendToHistory(command);
            }
            string trimmedCommand = command.Trim();
            if (!TryRunBuiltin(trimmedCommand))
            {
                RunRemoteCommand(command);
            }
        }
        catch (Exception exception)
        {
            _lastCommandFailed = true;
            ProcessOutput(AnsiEscapeUtils.Colorize(exception.Message, 1));
            ShowPrompt();
            return true;
        }
        return true;
    }

    /// <summary>
    /// Every command runs in a channel of its own, so commands that change the state of the shell are carried out here: cd, export and
    /// unset are remembered for the next commands, exit closes the tab, clear clears the screen and history lists the history.
    /// </summary>
    private bool TryRunBuiltin(string command)
    {
        List<ShellCommandLine.Token> tokens = ShellCommandLine.Tokenize(command);
        if (tokens.Count == 0 || tokens[0].IsOperator)
        {
            return false;
        }
        string name = tokens[0].Text;
        bool compound = ShellCommandLine.HasSeparators(tokens);
        if (compound)
        {
            if (name == "cd" || name == "export" || name == "unset")
            {
                ProcessOutput(AnsiEscapeUtils.Colorize($"Note: '{name}' in a compound command does not affect the next commands; type it on a line of its own.", 3) + "\r\n");
            }
            return false;
        }
        switch (name)
        {
            case "cd":
                RunChangeDirectoryCommand(command.Substring(tokens[0].End));
                ShowPrompt();
                return true;
            case "export":
            case "unset":
                {
                    if (!ShellCommandLine.TryParseVariableCommand(command, out List<KeyValuePair<string, string>> assignments))
                    {
                        return false;
                    }
                    foreach (KeyValuePair<string, string> assignment in assignments)
                    {
                        _environment.Set(assignment);
                    }
                    ShowPrompt();
                    return true;
                }
            case "exit":
            case "logout":
                if (EndOfInputRequested == null)
                {
                    return false;
                }
                RequestEndOfInput();
                return true;
            case "clear":
                if (tokens.Count != 1)
                {
                    return false;
                }
                ClearScreen();
                return true;
            case "history":
                if (tokens.Count != 1)
                {
                    return false;
                }
                {
                    List<string> history = BuildHistory();
                    history.Reverse();
                    PrintLines(history.Select((string item, int index) => $"{index + 1,5}  {item}"));
                    ShowPrompt();
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Opening the channel takes several round trips to the server, so it is done on a worker thread. The prompt stays hidden until the
    /// channel is ready, so no second command can start meanwhile.
    /// </summary>
    private void RunRemoteCommand(string command)
    {
        SshSession session = _context.Session;
        // In byte mode the PTY is opened without IUTF8 (see SshTerminalControl.RunRemoteCommand).
        bool utf8Tty = !ByteOrientedLineEditing;
        string remoteCommand = BuildRemoteCommand(command);
        ThreadPool.QueueUserWorkItem(delegate
        {
            SshShellChannel sshShellChannel = null;
            Exception failure = null;
            try
            {
                sshShellChannel = session.OpenShellChannel(remoteCommand, "xterm", ExtendedDataMode.Merge, utf8Tty: utf8Tty);
                if (sshShellChannel == null)
                {
                    failure = new Exception("Failed to open an SSH channel");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            bool posted = false;
            try
            {
                if (!IsDisposed)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => CompleteRemoteCommand(command, sshShellChannel, failure)));
                    posted = true;
                }
            }
            catch (InvalidOperationException)
            {
                // The application is shutting down.
            }
            if (!posted)
            {
                DisposeChannelQuietly(sshShellChannel);
            }
        });
    }

    private static void DisposeChannelQuietly(SshShellChannel channel)
    {
        if (channel == null)
        {
            return;
        }
        try
        {
            channel.Dispose();
        }
        catch
        {
        }
    }

    // On the UI thread.
    private void CompleteRemoteCommand(string command, SshShellChannel sshShellChannel, Exception failure)
    {
        if (IsDisposed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                DisposeChannelQuietly(sshShellChannel);
            });
            return;
        }
        try
        {
            if (failure != null)
            {
                throw failure;
            }
            CommandRun run = new CommandRun(this, command, _currentDirectory, Model.Document.LineCount - 1);
            _runs.Add(run);
            CommandChannel commandChannel = new CommandChannel(sshShellChannel, this, run);
            _channel = commandChannel;
            Model.ResetByteColumns();
            sshShellChannel.Finished += (object sender) =>
            {
                // Read on the session thread: the exit status may still be on its way when the channel reports EOF.
                bool connectionLost = sshShellChannel.ClosedByConnectionLoss;
                bool disposed = sshShellChannel.IsDisposed;
                bool statusKnown = connectionLost || disposed || sshShellChannel.HasExitStatus || sshShellChannel.ExitSignal != null;
                int exitStatus = (connectionLost || disposed) ? 0 : sshShellChannel.ExitStatus;
                OnCommandFinished(commandChannel, connectionLost, exitStatus, statusKnown);
            };
            sshShellChannel.PollingEnabled = true;
            sshShellChannel.ResizePty(GridSize.X, GridSize.Y);
        }
        catch (Exception exception)
        {
            ProcessOutput(AnsiEscapeUtils.Colorize(exception.Message, 1));
            if (_channel == null)
            {
                // Nothing runs: the channel was never attached, so it is closed here and the prompt comes back.
                DisposeChannelQuietly(sshShellChannel);
                _lastCommandFailed = true;
                PrintLinePasteNote();
                ShowPrompt();
            }
        }
    }

    private void OnCommandFinished(CommandChannel commandChannel, bool connectionLost, int exitStatus, bool statusKnown)
    {
        PostToUi(() =>
        {
            commandChannel?.DisposeInBackground();
            if (_channel != commandChannel)
            {
                return;
            }
            Model.ResetLinkTracking();
            Model.ResetByteColumns();
            _channel = null;
            _lastCommandFailed = connectionLost || (statusKnown && exitStatus != 0);
            string message = null;
            if (connectionLost)
            {
                message = AnsiEscapeUtils.Colorize("Connection lost.", 1);
            }
            else if (!(_context.VTSettings?.ShowExitCodes ?? true))
            {
                // Turned off in Smart Terminal Settings.
            }
            else if (!statusKnown)
            {
                message = AnsiEscapeUtils.Colorize("[exit status not reported]", 3);
            }
            else if (exitStatus > 0)
            {
                message = AnsiEscapeUtils.Colorize($"[exit code {exitStatus}]", 3);
            }
            else if (exitStatus < 0)
            {
                message = AnsiEscapeUtils.Colorize("[terminated by a signal]", 3);
            }
            if (message != null)
            {
                ProcessOutput((CursorCell.X > 0 ? "\r\n" : "") + message + "\r\n");
            }
            PrintLinePasteNote();
            ShowPrompt();
        });
    }

    // The output of a command and its end reach the UI in order, through one queue that is worked off with a time budget.
    private void PostToUi(Action action)
    {
        bool queueWasEmpty;
        lock (_uiQueue)
        {
            queueWasEmpty = _uiQueue.Count == 0;
            _uiQueue.Enqueue(action);
        }
        if (!queueWasEmpty)
        {
            return;
        }
        try
        {
            if (!IsDisposed)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DrainUiQueue));
                return;
            }
        }
        catch (InvalidOperationException)
        {
        }
        lock (_uiQueue)
        {
            _uiQueue.Clear();
        }
    }

    private void DrainUiQueue()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Action action;
            lock (_uiQueue)
            {
                if (_uiQueue.Count == 0)
                {
                    return;
                }
                action = _uiQueue.Dequeue();
            }
            if (!IsDisposed)
            {
                try
                {
                    action();
                }
                catch
                {
                }
            }
            if (stopwatch.Elapsed >= DrainBudget)
            {
                break;
            }
        }
        bool hasMore;
        lock (_uiQueue)
        {
            hasMore = _uiQueue.Count != 0;
        }
        if (hasMore && !IsDisposed)
        {
            // At a lower priority than input and drawing, so that a flood of output never freezes the window.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainUiQueue));
            return;
        }
        if (hasMore)
        {
            lock (_uiQueue)
            {
                _uiQueue.Clear();
            }
        }
    }

    // The paste.

    public override void PasteSpecial(PasteMode mode)
    {
        if (mode == PasteMode.LineByLine)
        {
            PasteLineByLine();
            return;
        }
        base.PasteSpecial(mode);
    }

    /// <summary>
    /// Pastes the clipboard text as commands that run one after another: the next one starts only when the previous one has ended, and the
    /// paste stops at the first command that fails (unless that is turned off in the settings) or when it is cancelled.
    /// </summary>
    public bool PasteLineByLine()
    {
        if (_prompt == null || _linePaste != null)
        {
            MessageBoxes.Info("The terminal is busy with another command. Wait until it ends and try again.");
            return false;
        }
        try
        {
            // Every command runs when its turn comes, so the confirmation and the warnings of a paste apply.
            string text = PasteGuard.Prepare(Win32Clipboard.GetText(PasteAnchor.Handle), AppSettings, linesRunAtOnce: true, owner: PasteAnchor);
            if (text == null)
            {
                return false;
            }
            List<string> commands = ShellCommandLine.SplitPastedCommands(text);
            if (commands.Count == 0)
            {
                return false;
            }
            LinePaste run = new LinePaste(commands, PasteGuard.OptionsOf(AppSettings).LineByLineStopOnError);
            _linePaste = run;
            run.Progress = new PasteProgressWindow("Paste line by line");
            run.Progress.CancelRequested += (object sender, EventArgs e) => CancelActivePaste();
            run.Progress.ShowNear(PasteAnchor);
            RunNextPastedLine();
            return true;
        }
        catch (Exception exception)
        {
            EndLinePaste();
            MessageBoxes.Exception(exception);
            return false;
        }
    }

    // At a prompt: runs the next pasted command, or ends the paste.
    private void RunNextPastedLine()
    {
        LinePaste run = _linePaste;
        if (run == null || IsDisposed || _prompt == null || _channel != null)
        {
            return;
        }
        if (run.Run.IsOver(_lastCommandFailed))
        {
            EndLinePaste();
            return;
        }
        string command = run.Run.TakeNext(out string progressText);
        run.Progress?.SetProgress(run.Run.Started - 1, run.Run.Total, progressText);
        Submit(command);
    }

    private void EndLinePaste()
    {
        LinePaste run = _linePaste;
        _linePaste = null;
        IPasteProgress progress = run?.Progress;
        if (progress == null)
        {
            return;
        }
        try
        {
            progress.Close();
            progress.Dispose();
        }
        catch
        {
        }
    }

    // After a command has ended: when the paste line by line has to stop here, says so (and ends it).
    private void PrintLinePasteNote()
    {
        LinePaste run = _linePaste;
        if (run == null)
        {
            return;
        }
        LinePasteStep step = run.Run.AfterCommand(_lastCommandFailed, out string note);
        if (step == LinePasteStep.EndSilently)
        {
            EndLinePaste();
            return;
        }
        if (step == LinePasteStep.Continue)
        {
            return;
        }
        EndLinePaste();
        ProcessOutput((CursorCell.X > 0 ? "\r\n" : "") + AnsiEscapeUtils.Colorize(note, 3) + "\r\n");
    }

    protected override bool CancelActivePaste()
    {
        LinePaste run = _linePaste;
        if (run != null && run.Run.Cancel())
        {
            run.Progress?.SetProgress(run.Run.Started, run.Run.Total, "Cancelling after the current command...");
            if (_prompt != null)
            {
                EndLinePaste();
            }
            return true;
        }
        return base.CancelActivePaste();
    }

    public override void InsertCommand(string text)
    {
        if (_prompt == null)
        {
            // A program runs: the text goes to it as typed keys.
            base.InsertCommand(text);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (_historySearch != null)
        {
            _historySearch.Append(text);
            ShowHistorySearchLine();
            return;
        }
        PrepareInputForEditing();
        _prompt.Insert(text);
    }

    public override void PasteText(string clipboardText)
    {
        if (_prompt == null)
        {
            base.PasteText(clipboardText);
            return;
        }
        try
        {
            // The command line is a single line: pasted lines are joined the way the shell would run them. The text only lands in the
            // command line and nothing runs until Enter, so it is cleaned and only a large paste is confirmed.
            string text = PasteGuard.Prepare(PasteGuard.ApplyMode(clipboardText, PasteModeInUse), AppSettings, linesRunAtOnce: false, owner: PasteAnchor);
            text = ShellCommandLine.JoinPastedLines(text);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (_historySearch != null)
            {
                _historySearch.Append(text);
                ShowHistorySearchLine();
                return;
            }
            PrepareInputForEditing();
            _prompt.Insert(text);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    protected override void OnDisposing()
    {
        // Closing the tab stops the command that is running in it (tail -f, top, ...).
        EndLinePaste();
        CommandChannel channel = _channel;
        _channel = null;
        channel?.DisposeInBackground();
        try
        {
            CloseCompletion();
        }
        catch
        {
        }
    }
}
