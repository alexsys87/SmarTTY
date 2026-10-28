using SmarTTY.Ssh;
using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public class SshTerminalControl : TerminalEmulatorControl, SmarTTY.GUI.Backend.IFileListHost
{
    public class InputRegion
    {
        private readonly SshTerminalControl _terminal;

        private readonly int _startColumn;

        private readonly int _startLine;

        public TextDocument.TextPosition StartPosition => PositionAt(0);

        public string Text
        {
            get
            {
                StringBuilder stringBuilder = new StringBuilder();
                EditableTextDocument editableTextDocument = ((EditableTextBrowser)_terminal).EditableDocument;
                for (int editableTextDocumentIndex = _startLine; editableTextDocumentIndex < editableTextDocument.LineCount; editableTextDocumentIndex++)
                {
                    string value = editableTextDocument.GetLineText(editableTextDocumentIndex);
                    stringBuilder.Append(value);
                }
                return stringBuilder.ToString().Substring(_startColumn);
            }
            set
            {
                EditableTextDocument document = ((EditableTextBrowser)_terminal).EditableDocument;
                _terminal.ReplaceText(StartPosition, document.EndPosition, select: false, value ?? "");
            }
        }

        public int CaretIndex
        {
            get
            {
                return IndexOf(_terminal.CaretPosition);
            }
            set
            {
                _terminal.SetCaret(PositionAt(value));
            }
        }

        public InputRegion(SshTerminalControl sshTerminalControl, int startLine, int startColumn)
        {
            _terminal = sshTerminalControl;
            this._startColumn = startColumn;
            _startLine = startLine;
        }

        public int IndexOf(TextDocument.TextPosition textPosition)
        {
            EditableTextDocument editableTextDocument = ((EditableTextBrowser)_terminal).EditableDocument;
            int offset = -this._startColumn;
            for (int line = _startLine; line < editableTextDocument.LineCount; line++)
            {
                int length = editableTextDocument.GetLineText(line).Length;
                if (line == textPosition.Line)
                {
                    return offset + textPosition.Column;
                }
                offset += length;
            }
            return -1;
        }

        public TextDocument.TextPosition PositionAt(int index)
        {
            if (index >= 0)
            {
                int targetColumn = index + this._startColumn;
                int charsBefore = 0;
                for (int line = _startLine; line < ((EditableTextBrowser)_terminal).EditableDocument.LineCount; line++)
                {
                    int length = ((EditableTextBrowser)_terminal).EditableDocument.GetLineText(line).Length;
                    if (targetColumn >= charsBefore && targetColumn <= charsBefore + length)
                    {
                        return new TextDocument.TextPosition(targetColumn - charsBefore, line);
                    }
                    charsBefore += length;
                }
            }
            return ((EditableTextBrowser)_terminal).EditableDocument.EndPosition;
        }
    }

    private class InputLine
    {
        public readonly InputRegion Region;

        public readonly int StartLine;

        public readonly string Prompt;

        private readonly SshTerminalControl _terminal;

        private List<string> _history;

        private int _historyIndex;

        public TextDocument.TextPosition StartPosition => Region.StartPosition;

        public string Text => Region.Text;

        public bool IsAtWordStart
        {
            get
            {
                int index = Region.IndexOf(_terminal.CaretPosition);
                string text = Text;
                if (text != null && index >= 0 && index <= text.Length)
                {
                    if (index == 0)
                    {
                        return true;
                    }
                    if (text[index - 1] == ' ')
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public InputLine(SshTerminalControl sshTerminalControl, int startLine, string text)
        {
            _terminal = sshTerminalControl;
            this.StartLine = startLine;
            Region = new InputRegion(sshTerminalControl, startLine, text.Length);
            Prompt = text;
        }

        public void SetText(string text)
        {
            Region.Text = text;
            Region.CaretIndex = -1;
        }

        public void NavigateHistory(bool older)
        {
            string text = this.Text ?? "";
            if (_history == null)
            {
                _history = BuildHistory();
                _history.Remove(text);
                _history.Insert(0, text);
                _historyIndex = 0;
            }
            else if (_historyIndex >= 0 && _historyIndex < _history.Count && (_historyIndex == 0 || text != ""))
            {
                // Edits of the line being typed (entry 0) and of recalled commands are kept while navigating.
                _history[_historyIndex] = text;
            }
            // Like bash, Up stops at the oldest command and Down at the line being typed: wrapping around used to
            // replace the typed line with the oldest command when Down was pressed at the prompt.
            int newIndex = _historyIndex + (older ? 1 : -1);
            if (newIndex < 0 || newIndex >= _history.Count)
            {
                return;
            }
            _historyIndex = newIndex;
            try
            {
                SetText(_history[_historyIndex]);
            }
            catch
            {
            }
        }

        internal List<string> BuildHistory()
        {
            return _terminal.BuildHistory();
        }
    }

    private class CommandChannel : IDisposable
    {

        private SshShellChannel _channel;

        private SshTerminalControl _terminal;

        public readonly CommandRun Run;

        private Utf8StreamDecoder _decoder = new Utf8StreamDecoder();

        private bool _outputStarted;

        public CommandChannel(SshShellChannel sshShellChannel, SshTerminalControl sshTerminalControl, CommandRun commandRun)
        {
            _channel = sshShellChannel;
            this._terminal = sshTerminalControl;
            this.Run = commandRun;
            sshShellChannel.DataReceived += OnDataReceived;
        }

        private void OnDataReceived(object sender, byte[] buffer, int offset, int count, int flags)
        {
            // In byte mode the terminal needs to see which stray byte arrived (see Utf8StreamDecoder).
            _decoder.EscapeInvalidBytes = _terminal.ByteOrientedLineEditing;
            string text = _decoder.Decode(buffer, offset, count);
            if (!this._outputStarted)
            {
                text = text.TrimStart('\r', '\n');
                if (text == "")
                {
                    return;
                }
                this._outputStarted = true;
            }
            this._terminal.PostToUi(() =>
            {
                if (this._terminal._channel == this)
                {
                    this._terminal.ProcessOutput(text);
                }
            });
        }

        public void SendInput(byte[] buffer, InputKind inputKind)
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

        public void Dispose()
        {
            _channel.Dispose();
        }
    }

    private enum CompletionTrigger
    {
        Normal,
        Auto,
        Options,
        Tab
    }

    private ITerminalSessionContext _context;

    private readonly string _homeDirectory;

    private bool _welcomeShown;

    private HashSet<string> _linkExtensions;

    private InputLine _inputLine;

    private CommandChannel _channel;

    // "Paste line by line": the commands that are still to be run, one after another.
    private sealed class LinePaste
    {
        public readonly Queue<string> Pending;

        public readonly int Total;

        public readonly bool StopOnError;

        public int Started;

        public bool Cancelled;

        public IPasteProgress Progress;

        public LinePaste(List<string> commands, bool stopOnError)
        {
            Pending = new Queue<string>(commands);
            Total = commands.Count;
            StopOnError = stopOnError;
        }
    }

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

    private List<string> _typedCommands = new List<string>();

    // Variables set with "export" (a null value: "unset"), in the order they were set: a later value may refer to an
    // earlier one. They are applied in front of every command, since each command runs in a new channel.
    private List<KeyValuePair<string, string>> _environment = new List<KeyValuePair<string, string>>();

    private List<CommandRun> _runs = new List<CommandRun>();

    private Queue<Action> _uiQueue = new Queue<Action>();

    // Asked from the session every time: a reconnect replaces the analyzer (and the SSH session it uses).
    internal ShellCompletionAnalyzer ShellAnalyzer => _context.ProvideAnalyzer();

    private ICompletionPopup _popup;

    // "/home/user2" is not under the home directory "/home/user": the prefix must end at a path boundary.
    private string PromptPrefix => _context.TitleWithUserName + ":" + ShellCommandLine.AbbreviateHome(_currentDirectory, _homeDirectory);

    protected virtual bool IsCommandRunning => _inputLine == null;

    // At the prompt (_inputLine != null) the user edits the local input line, so keys must NOT be
    // forwarded to the remote channel; only while a command is running do they go to the terminal.
    protected override bool UsesTerminalInput => _inputLine == null;

    protected virtual bool HasInputLine => _inputLine != null;

    public string CurrentDirectory => _currentDirectory;

    public SshSession Session => _context.Session;

    public bool HasEnvironment => _environment.Count > 0;

    /// <summary>Variables exported in this tab (for completing $NAME).</summary>
    public IEnumerable<string> EnvironmentNames => _environment.ToArray().Where((KeyValuePair<string, string> item) => item.Value != null).Select((KeyValuePair<string, string> item) => item.Key);

    public string HomeDirectory => _homeDirectory;

    public VirtualTerminalSettings VtSettings => _context.VTSettings;

    public virtual bool IsAtPrompt => _inputLine != null;

    public event Action<string> DirectoryChanged;

    public event EventHandler<OpenFileEventArgs> OpenFileRequested;

    public event EventHandler EndOfInputRequested;

    public event EventHandler<CompletionPopupRequest> CompletionPopupRequested;

    public SshTerminalControl(ITerminalSessionContext terminalSessionContext, string initialDirectory = null)
    {
        _context = terminalSessionContext;
        _homeDirectory = (_currentDirectory = terminalSessionContext.Session.RunCommandChecked("pwd", requireSuccess: true)?.Where((string text) => text != "")?.LastOrDefault());
        if (initialDirectory != null)
        {
            _currentDirectory = initialDirectory;
        }
        _linkExtensions = new HashSet<string>();
        string[] parts = (terminalSessionContext.VTSettings.AutoLinkExtensions ?? "").Split(';');
        foreach (string item in parts)
        {
            _linkExtensions.Add(item);
        }
        if (_homeDirectory == null)
        {
            throw new RemoteOperationException("Failed to query home directory. Please try disabling virtual mode.");
        }
        // The horizontal scroll bar sits right under the terminal: leave a few rows free so that selecting the last lines
        // for copying does not end up on it (touching it dropped the selection).
        BottomGapRows = 3;
        UpdateTitle();
        TerminalSizeChanged += OnTerminalSizeChanged;
        base.DataEntered += OnDataEntered;
        // Starts loading the history and the list of executables in the background.
        terminalSessionContext.ProvideAnalyzer();
    }

    // Newest first, without duplicates: the history file of the session followed by what was typed in this tab.
    private List<string> BuildHistory()
    {
        string[] items = ShellAnalyzer?.GetHistory() ?? new string[0];
        List<string> list = new List<string>();
        HashSet<string> hashSet = new HashSet<string>();
        foreach (string item in items.Concat(_typedCommands).Reverse())
        {
            if (hashSet.Add(item))
            {
                list.Add(item);
            }
        }
        return list;
    }

    public void RequestOpenFile(string text, int? line, int? column)
    {
        OpenFileRequested?.Invoke(this, new OpenFileEventArgs
        {
            Path = text,
            Column = column,
            Line = line
        });
    }

    private void OnDataEntered(object sender, byte[] buffer, InputKind inputKind)
    {
        _channel?.SendInput(buffer, inputKind);
    }

    private void OnTerminalSizeChanged(TerminalEmulatorControl terminalEmulatorControl, int columns, int rows)
    {
        _channel?.Resize(columns, rows);
    }

    public bool IsLinkExtension(string text)
    {
        return _linkExtensions?.Contains(text) ?? false;
    }

    private void UpdateTitle()
    {
        base.Title = PromptPrefix;
    }

    /// <summary>
    /// Changes the directory of the tab to a literal path (absolute or relative to the current directory), as the file
    /// list does. The line being typed is kept; while a command is running only the next prompt is affected.
    /// Throws if the directory cannot be entered.
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
        List<string> output = _context.Session.RunCommand("cd " + RemotePathUtils.ShellQuote(target), out int? exitCode);
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
        if (_inputLine != null)
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
        // The tab title shows the directory as well.
        UpdateTitle();
        DirectoryChanged?.Invoke(directory);
    }

    /// <summary>
    /// Runs a typed "cd" command. The remote shell resolves the argument (~, $VAR, CDPATH, globs, quotes) and reports
    /// the resulting directory, so whatever works in bash works here too. "cd -" goes back to the previous directory.
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
                PrintLines(new[] { TerminalEmulatorControl.Colorize("cd: OLDPWD not set", 1) });
                return;
            }
            resolvedArguments = RemotePathUtils.ShellQuote(_previousDirectory);
        }
        List<string> output = _context.Session.RunCommand(BuildRemoteCommand("cd " + resolvedArguments + " >/dev/null && pwd", interactive: false), out int? exitCode);
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
            PrintLines(errors.Select((string line) => TerminalEmulatorControl.Colorize(line, 1)));
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

    // Redraws the prompt (after the directory has changed) without losing what has been typed.
    private void RefreshPrompt()
    {
        InputLine inputLine = _inputLine;
        if (inputLine == null)
        {
            return;
        }
        if (_historySearch != null)
        {
            EndHistorySearch(accept: true);
            inputLine = _inputLine;
        }
        ReplacePromptLine(null, inputLine.Text, inputLine.Region.CaretIndex);
    }

    /// <summary>
    /// Replaces the prompt line (with a custom prompt, or the normal one when <paramref name="prompt"/> is null) and
    /// puts <paramref name="text"/> on it.
    /// </summary>
    private void ReplacePromptLine(string prompt, string text, int caretIndex)
    {
        InputLine inputLine = _inputLine;
        if (inputLine == null)
        {
            return;
        }
        text = text ?? "";
        CloseCompletion();
        EditableTextDocument document = ((EditableTextBrowser)this).EditableDocument;
        document.RemoveLines(inputLine.StartLine, document.LineCount - inputLine.StartLine);
        _inputLine = null;
        ShowPrompt(removeTrailingBlankLine: false, customPrompt: prompt);
        _inputLine.SetText(text);
        _inputLine.Region.CaretIndex = (caretIndex >= 0 && caretIndex <= text.Length) ? caretIndex : text.Length;
    }

    private void StartHistorySearch()
    {
        if (_inputLine == null)
        {
            return;
        }
        _searchOriginalText = _inputLine.Text ?? "";
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
    private bool HandleHistorySearchKey(KeyEventArgs e)
    {
        HistorySearch search = _historySearch;
        switch (e.KeyCode)
        {
            case Keys.R when e.Control && !e.Alt:
                search.Older();
                ShowHistorySearchLine();
                return true;
            case Keys.S when e.Control && !e.Alt:
                search.Newer();
                ShowHistorySearchLine();
                return true;
            case Keys.G when e.Control && !e.Alt:
            case Keys.Escape:
                EndHistorySearch(accept: false);
                return true;
            case Keys.C when e.Control && !e.Alt:
                _searchOriginalText = "";
                EndHistorySearch(accept: false);
                return true;
            case Keys.Back:
                search.RemoveLast();
                ShowHistorySearchLine();
                return true;
            case Keys.Return:
                EndHistorySearch(accept: true);
                SubmitInput();
                return true;
        }
        return false;
    }

    private static bool IsModifierKey(Keys keyCode)
    {
        return keyCode == Keys.ShiftKey || keyCode == Keys.ControlKey || keyCode == Keys.Menu || keyCode == Keys.LWin || keyCode == Keys.RWin || keyCode == Keys.Capital;
    }

    // Keys that leave the history search keeping the found command for editing, and then act as usual (as in bash).
    private static bool IsEditingKey(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.Tab:
            case Keys.Delete:
            case Keys.Prior:
            case Keys.Next:
            case Keys.Insert:
                return true;
        }
        // Other Ctrl shortcuts as well, but not Ctrl+Alt (AltGr), which types characters on many layouts.
        return e.Control && !e.Alt;
    }

    private void ShowPrompt(bool removeTrailingBlankLine = true, string customPrompt = null)
    {
        ActiveBufferIndex = 0;
        if (removeTrailingBlankLine && ((EditableTextBrowser)this).EditableDocument.LineCount > 0 && ((EditableTextBrowser)this).EditableDocument.GetLineText(((EditableTextBrowser)this).EditableDocument.LineCount - 1).Trim() == "")
        {
            ((EditableTextBrowser)this).EditableDocument.RemoveLines(((EditableTextBrowser)this).EditableDocument.LineCount - 1, 1);
        }
        char ch = '$';
        if (_context.IsRoot)
        {
            ch = '#';
        }
        _inputLine = new InputLine(this, ((EditableTextBrowser)this).EditableDocument.LineCount, customPrompt ?? $"{PromptPrefix}{ch} ");
        ((EditableTextBrowser)this).EditableDocument.AppendLine(_inputLine.Prompt);
        RewrapInput();
        OnTextReplaced();
        SetCaret(((EditableTextBrowser)this).EditableDocument.EndPosition);
        NavigationLocked = false;
        if (_linePaste != null && IsHandleCreated && !IsDisposed)
        {
            // The next pasted command, once this prompt is on the screen (not from inside the command that has just ended).
            BeginInvoke(new Action(RunNextPastedLine));
        }
    }

    public void Start()
    {
        if (!_welcomeShown)
        {
            ProcessOutput("Welcome to Smart Terminal.");
            _welcomeShown = true;
            ShowPrompt();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
    }

    private ILinkTarget GetLinkAt(int line, Point point)
    {
        LineInfo lineInfo = ActiveBuffer?.Lines?.GetLine(line);
        if (lineInfo?.Cells != null)
        {
            TextDocument.TextPosition textPosition = HitTest(point);
            if (textPosition.Column >= 0 && textPosition.Column < lineInfo.Cells.Length)
            {
                return lineInfo.Cells[textPosition.Column]._link.Target;
            }
        }
        return null;
    }

    protected override HoverTarget GetHoverTarget(int line, Point point)
    {
        ILinkTarget linkTarget = GetLinkAt(line, point);
        if (linkTarget != null && linkTarget.IsActive)
        {
            return new HoverTarget
            {
                Cursor = Cursors.Hand,
                ClickAction = linkTarget.Activate
            };
        }
        return base.GetHoverTarget(line, point);
    }

    private bool IsInInputArea(TextDocument.TextPosition textPosition)
    {
        if (_inputLine != null)
        {
            return !(textPosition < _inputLine.StartPosition);
        }
        return false;
    }

    protected override int GetScreenSelectionLastLine()
    {
        // Ctrl+double click selects the visible output, not the command line the user is editing.
        if (_inputLine != null)
        {
            return _inputLine.StartLine - 1;
        }
        return base.GetScreenSelectionLastLine();
    }

    private bool InputWraps()
    {
        // Before the first layout there are no columns yet; rewrapping to 0 columns would never finish.
        if (_inputLine == null || _visibleColumns <= 0)
        {
            return false;
        }
        for (int startLine = _inputLine.StartLine; startLine < ((EditableTextBrowser)this).EditableDocument.LineCount; startLine++)
        {
            int length = ((EditableTextBrowser)this).EditableDocument.GetLineText(startLine).Length;
            bool isLastLine = startLine == ((EditableTextBrowser)this).EditableDocument.LineCount - 1;
            if (length > _visibleColumns)
            {
                return true;
            }
            if (!isLastLine && length != _visibleColumns)
            {
                return true;
            }
        }
        return false;
    }

    protected override void OnResize(EventArgs e)
    {
        int? caretIndex = _inputLine?.Region?.CaretIndex;
        base.OnResize(e);
        if (!RewrapInput(caretIndex) && caretIndex.HasValue && _inputLine != null)
        {
            _inputLine.Region.CaretIndex = caretIndex.Value;
        }
    }

    public override void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (IsInInputArea(start) && (select || IsInInputArea(end)) && _inputLine != null)
        {
            int startIndex = _inputLine.Region.IndexOf(start);
            int endIndex = ((!select) ? _inputLine.Region.IndexOf(end) : startIndex);
            if (endIndex < startIndex)
            {
                int swapIndex = endIndex;
                endIndex = startIndex;
                startIndex = swapIndex;
            }
            ReplaceTextCore(start, end, select, text);
            RewrapInput();
            string filter = _inputLine?.Text;
            _popup?.SetFilter(filter);
            CompletionSession completionSession = _completion;
            if (completionSession != null && !_applyingCompletion && completionSession.OnTextChanged(filter, startIndex, endIndex - startIndex, (text ?? "").Replace("\n", "").Replace("\r", "").Length))
            {
                _completion.Dispose();
                _completion = null;
            }
        }
        else
        {
            base.ReplaceText(start, end, select, text);
        }
    }

    private bool RewrapInput(int? caretIndex = null)
    {
        if (!InputWraps())
        {
            return false;
        }
        InputLine inputLine = _inputLine;
        string text = inputLine?.Prompt + inputLine?.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        int resolvedCaretIndex = caretIndex ?? _inputLine.Region.CaretIndex;
        StringBuilder stringBuilder = new StringBuilder();
        for (int textIndex = 0; textIndex < text.Length; textIndex += _visibleColumns)
        {
            int length = Math.Min(text.Length - textIndex, _visibleColumns);
            stringBuilder.Append(text.Substring(textIndex, length));
            if (textIndex + _visibleColumns < text.Length)
            {
                stringBuilder.Append("\n");
            }
        }
        ReplaceTextCore(new TextDocument.TextPosition(0, inputLine.StartLine), new TextDocument.TextPosition(0, ((EditableTextBrowser)this).EditableDocument.LineCount), select: false, stringBuilder.ToString());
        OnTextReplaced();
        inputLine.Region.CaretIndex = resolvedCaretIndex;
        return true;
    }

    protected override void PlaceCaretAt(Point point)
    {
        if (_inputLine != null)
        {
            TextDocument.TextPosition textPosition = HitTest(point, allowPastEnd: true);
            if (textPosition.IsValid && IsInInputArea(textPosition))
            {
                SetCaret(textPosition);
            }
        }
        base.PlaceCaretAt(point);
    }

    private int FindWordBoundary(int position, string text, bool backward)
    {
        if (backward)
        {
            do
            {
                position--;
            }
            while (position > 0 && IsWordChar(text[position - 1]) && IsWordChar(text[position]));
            if (position < 0)
            {
                position = 0;
            }
        }
        else
        {
            do
            {
                position++;
            }
            while (position > 0 && position < text.Length && IsWordChar(text[position - 1]) && IsWordChar(text[position]));
            if (position > text.Length)
            {
                position = text.Length;
            }
        }
        return position;
    }

    protected override TextDocument.TextPosition ComputeMovedPosition(CaretMove caretMove, Keys keys, TextDocument.TextPosition textPosition, bool extendSelection = false)
    {
        if (caretMove == CaretMove.Home || caretMove == CaretMove.End)
        {
            keys |= Keys.Control;
        }
        TextDocument.TextPosition movedPosition = base.ComputeMovedPosition(caretMove, keys, textPosition, extendSelection);
        if ((caretMove == CaretMove.WordLeft || caretMove == CaretMove.WordRight) && _inputLine != null)
        {
            int index = _inputLine.Region.IndexOf(textPosition);
            string text = _inputLine?.Text;
            // At the end of the line (index == text.Length) Ctrl+Left must still jump to the start of the last word.
            if (text != null && index >= 0 && index <= text.Length)
            {
                int boundary = FindWordBoundary(index, text, caretMove == CaretMove.WordLeft);
                movedPosition = _inputLine.Region.PositionAt(boundary);
            }
        }
        InputLine inputLine = _inputLine;
        if (inputLine != null && movedPosition < inputLine.StartPosition)
        {
            movedPosition = inputLine.StartPosition;
        }
        return movedPosition;
    }

    protected override void SetCaretCore(TextDocument.TextPosition textPosition, SelectionChangeSource selectionChangeSource, CaretDirection caretDirection = CaretDirection.Unspecified)
    {
        base.SetCaretCore(textPosition, selectionChangeSource, caretDirection);
        if (_inputLine != null)
        {
            SetSelectionEnd(textPosition);
        }
    }

    protected override void SelectAll()
    {
        if (_inputLine != null)
        {
            SetSelectionEnd(new TextDocument.TextPosition(((EditableTextBrowser)this).EditableDocument.GetLineText(((EditableTextBrowser)this).EditableDocument.LineCount - 1).Length, ((EditableTextBrowser)this).EditableDocument.LineCount - 1));
            SetSelection(select: true, new TextDocument.TextPosition(_inputLine.Prompt.Length, _inputLine.StartLine));
        }
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
        InputLine inputLine = _inputLine;
        if (inputLine == null)
        {
            return;
        }
        string text = inputLine.Text ?? "";
        int caretIndex = inputLine.Region.CaretIndex;
        if (caretIndex < 0 || caretIndex > text.Length)
        {
            caretIndex = text.Length;
        }
        // The word under the caret is replaced; when the caret is between words, an empty word at the caret is
        // completed (it used to be inserted at the start of the line for a command, e.g. after "ls | ").
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
        CompletionPopupRequest completionPopupRequest = new CompletionPopupRequest
        {
            ScreenLocation = PointToScreen(Point.Round(PositionToPoint(CaretPosition.Column, CaretPosition.Line))),
            LineHeight = LineHeight,
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
        InputRegion inputRegion = _inputLine?.Region;
        if (inputRegion != null)
        {
            // The session updates itself; reporting this replacement to it as typing used to end it.
            _applyingCompletion = true;
            try
            {
                inputRegion.Text = item.NewText;
                inputRegion.CaretIndex = item.CaretIndex;
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

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_historySearch != null && _inputLine != null)
        {
            // Typed characters extend the search query instead of the command line.
            if (e.KeyChar >= ' ')
            {
                _historySearch.Append(e.KeyChar);
                ShowHistorySearchLine();
            }
            e.Handled = true;
            return;
        }
        bool? isAtWordStart = null;
        if (_inputLine != null)
        {
            if (e.KeyChar >= ' ' || e.KeyChar == '\b')
            {
                PrepareInputForEditing();
            }
            switch (e.KeyChar)
            {
                case '\r':
                    if (SubmitInput())
                    {
                        e.Handled = true;
                        return;
                    }
                    break;
                case '\t':
                    {
                        CompletionSession completionSession = _completion;
                        if (completionSession == null || !completionSession.IsOpen)
                        {
                            ShowCompletions(CompletionTrigger.Tab);
                        }
                        else
                        {
                            // The list is open (e.g. after a space) but Tab did not pick an item from it.
                            completionSession.ApplyOnTab();
                        }
                        e.Handled = true;
                        return;
                    }
                default:
                    if (e.KeyChar > ' ' && AtWordStartTriggerEnabled())
                    {
                        isAtWordStart = _inputLine?.IsAtWordStart;
                    }
                    break;
            }
            CompletionSession activeCompletion = _completion;
            if (activeCompletion != null && activeCompletion.IsOpen && activeCompletion.HandleChar(e.KeyChar))
            {
                activeCompletion.Dispose();
                _completion = null;
                e.Handled = true;
                switch (e.KeyChar)
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
            }
            if (e.Handled)
            {
                return;
            }
        }
        base.OnKeyPress(e);
        if (_inputLine == null)
        {
            return;
        }
        switch (e.KeyChar)
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
                    ShowCompletions((e.KeyChar != '-') ? CompletionTrigger.Auto : CompletionTrigger.Options);
                }
                break;
            case '\t':
            case '\n':
            case '\r':
                break;
        }
    }

    private AutoCompletionTriggers CompletionTriggers()
    {
        return _context?.VTSettings?.CompletionTriggers ?? new AutoCompletionTriggers();
    }

    private bool AtWordStartTriggerEnabled()
    {
        return CompletionTriggers().FirstCharacterInWord;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_historySearch != null && _inputLine != null)
        {
            if (IsModifierKey(e.KeyCode))
            {
                base.OnKeyDown(e);
                return;
            }
            if (HandleHistorySearchKey(e))
            {
                SuppressKey(e);
                return;
            }
            if (!IsEditingKey(e))
            {
                // A character key: OnKeyPress adds it to the query.
                return;
            }
            EndHistorySearch(accept: true);
        }
        CompletionSession completionSession = _completion;
        ICompletionPopup completionPopup = _popup;
        CompletionSession.AppliedEventArgs appliedBefore = completionSession?.LastApplied;
        if ((completionPopup != null && completionPopup.TryHandleKeyEvent(e)) || (completionSession != null && completionSession.HandleKey(e)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            // Only an item applied by this very key counts: LastApplied stays set after Tab auto-completed a
            // directory, and every later arrow key in the popup used to restart the completion.
            CompletionSession.AppliedEventArgs appliedEventArgs = completionSession?.LastApplied;
            if (appliedEventArgs != null && appliedEventArgs != appliedBefore && appliedEventArgs.IsDirectory && e.KeyCode != Keys.Return && _completion == completionSession && _inputLine != null)
            {
                // A directory was chosen with Tab: list its contents right away.
                completionSession.Dispose();
                _completion = null;
                ShowCompletions();
            }
            return;
        }
        if (_inputLine != null)
        {
            if (e.KeyCode == Keys.Escape)
            {
                CloseCompletion();
            }
            if (e.Control && !e.Alt)
            {
                switch (e.KeyCode)
                {
                    case Keys.L:
                        {
                            string text = _inputLine.Text;
                            ClearScreen();
                            _inputLine?.SetText(text);
                            SuppressKey(e);
                            return;
                        }
                    case Keys.C:
                        // With a selection Ctrl+C copies it (the base class does that). Clearing the line first used
                        // to drop the selection, so nothing was copied and the typed command was lost.
                        if (!HasSelection)
                        {
                            CloseCompletion();
                            _inputLine.SetText("");
                            SuppressKey(e);
                            return;
                        }
                        break;
                    case Keys.D:
                        // As in bash, Ctrl+D closes the tab only on an empty line.
                        if (string.IsNullOrEmpty(_inputLine.Text))
                        {
                            RequestEndOfInput();
                        }
                        SuppressKey(e);
                        return;
                    case Keys.U:
                        DeleteInput(toStart: true);
                        SuppressKey(e);
                        return;
                    case Keys.K:
                        DeleteInput(toStart: false);
                        SuppressKey(e);
                        return;
                    case Keys.R:
                        // Ctrl+R searches the history as bash does; Ctrl+Shift+R shows it as a list.
                        if (e.Shift)
                        {
                            ShowHistoryPopup();
                        }
                        else
                        {
                            StartHistorySearch();
                        }
                        SuppressKey(e);
                        return;
                    case Keys.Space:
                        if (CompletionTriggers().CtrlSpace)
                        {
                            ShowCompletions();
                            SuppressKey(e);
                            return;
                        }
                        break;
                }
            }
            else if (!e.Alt && !e.Shift && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                _inputLine.NavigateHistory(e.KeyCode == Keys.Up);
                SuppressKey(e);
                return;
            }
            else if (e.KeyCode == Keys.Delete && !e.Shift)
            {
                PrepareInputForEditing();
            }
        }
        base.OnKeyDown(e);
    }

    private static void SuppressKey(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>
    /// Typing goes to the command line even when text above it is selected (e.g. to copy it): such a selection used to
    /// make every typed character, Backspace and Delete silently do nothing.
    /// </summary>
    private void PrepareInputForEditing()
    {
        if (_inputLine == null)
        {
            return;
        }
        if (!IsInInputArea(CaretPosition))
        {
            SetCaret(((EditableTextBrowser)this).EditableDocument.EndPosition);
        }
        if (HasSelection && !(IsInInputArea(SelectionStart) && IsInInputArea(SelectionEnd)))
        {
            SetSelection(select: false, CaretPosition);
            Invalidate();
        }
    }

    // Ctrl+U / Ctrl+K: delete from the caret to the start / end of the command line, as in readline.
    private void DeleteInput(bool toStart)
    {
        InputLine inputLine = _inputLine;
        if (inputLine == null)
        {
            return;
        }
        PrepareInputForEditing();
        string text = inputLine.Text ?? "";
        int caretIndex = inputLine.Region.CaretIndex;
        if (caretIndex < 0 || caretIndex > text.Length)
        {
            caretIndex = text.Length;
        }
        CloseCompletion();
        if (toStart)
        {
            inputLine.Region.Text = text.Substring(caretIndex);
            inputLine.Region.CaretIndex = 0;
        }
        else
        {
            inputLine.Region.Text = text.Substring(0, caretIndex);
            inputLine.Region.CaretIndex = caretIndex;
        }
    }

    private void ShowHistoryPopup()
    {
        CloseCompletion();
        CompletionPopupRequest completionPopupRequest = new CompletionPopupRequest
        {
            ScreenLocation = PointToScreen(Point.Round(PositionToPoint(CaretPosition.Column, CaretPosition.Line))),
            LineHeight = LineHeight,
            Items = _inputLine.BuildHistory()?.Select((string historyCommand) => new CompletionItem
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
            popup.ItemSelected += (CompletionItem completionItem, Keys keys) =>
            {
                if (_popup == popup)
                {
                    _popup = null;
                }
                if (_inputLine == null)
                {
                    return;
                }
                _inputLine.SetText(completionItem.Text);
                if (keys == Keys.Return)
                {
                    SubmitInput();
                }
            };
            popup.SetFilter(_inputLine?.Text);
        }
    }

    /// <summary>
    /// "Clear" of the context menu: the same as Ctrl+L. While a command runs nothing is cleared: the output of the command and
    /// the record of its run refer to lines of the screen.
    /// </summary>
    public override void ClearOutput()
    {
        if (_inputLine == null)
        {
            return;
        }
        string text = _inputLine.Text;
        ClearScreen();
        _inputLine?.SetText(text);
    }

    // Ctrl+L and "clear": an empty screen with the prompt at the top.
    private void ClearScreen()
    {
        _historySearch = null;
        CloseCompletion();
        Reset();
        ShowPrompt();
    }

    // Closing the tab disposes this control, so it is done after the key handler has returned.
    private void RequestEndOfInput()
    {
        if (EndOfInputRequested == null || IsDisposed)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            if (!IsDisposed)
            {
                EndOfInputRequested?.Invoke(this, EventArgs.Empty);
            }
            // The tab stayed open ("exit" was typed, closing was cancelled): give the prompt back.
            if (!IsDisposed && _inputLine == null && _channel == null)
            {
                ShowPrompt();
            }
        }));
    }

    private void BeginExecution()
    {
        _historySearch = null;
        _inputLine = null;
        ((EditableTextBrowser)this).EditableDocument.AppendLine("");
        NavigationLocked = true;
        SetCaret(new TextDocument.TextPosition(0, ((EditableTextBrowser)this).EditableDocument.LineCount - 1));
        ActiveBuffer.FirstRow = ((EditableTextBrowser)this).EditableDocument.LineCount - 1;
        base.CursorPosition = new Point(0, 0);
        UpdateCaret();
        UpdateLayout();
        CloseCompletion();
    }

    private void CloseCompletion()
    {
        _popup?.Close();
        _popup = null;
        _completion?.Dispose();
        _completion = null;
    }

    public string BuildRemoteCommand(string command, bool? interactive = null)
    {
        // Quoted as a single word: a directory name with a quote, $ or ` used to break every command.
        string text = "cd " + RemotePathUtils.ShellQuote(_currentDirectory) + " && ";
        interactive = interactive ?? _context.VTSettings.InteractiveModeWrapper;
        // "export A=1 && cmd" rather than "A=1 cmd": with the latter $A in the command itself was expanded before the
        // assignment ("echo $A" printed nothing) and the variable did not reach the other commands of a pipeline.
        StringBuilder exports = new StringBuilder();
        // A copy: completion threads build commands while the UI thread may change the list.
        foreach (KeyValuePair<string, string> item in _environment.ToArray())
        {
            exports.Append(item.Value == null ? "unset " + item.Key : "export " + item.Key + "=" + item.Value);
            exports.Append(" && ");
        }
        command = exports + command;
        if (interactive.Value)
        {
            // bash, or ash on OpenWrt, unless a shell is set by hand; until the shell of the host is known (or if it cannot be
            // found out) the command runs without the wrapper rather than in a shell that may be missing there.
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

    public bool Submit(string command)
    {
        if (_inputLine == null)
        {
            return false;
        }
        if (command != null)
        {
            _inputLine.SetText(command);
        }
        else
        {
            command = _inputLine?.Text;
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
            ProcessOutput(TerminalEmulatorControl.Colorize(exception.Message, 1));
            ShowPrompt();
            return true;
        }
        return true;
    }

    /// <summary>
    /// Every command runs in a channel of its own, so commands that change the state of the shell are carried out here:
    /// cd, export and unset are remembered for the next commands, exit closes the tab, clear clears the screen and
    /// history lists the history. A compound command ("cd /tmp && make") is run remotely as typed.
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
                ProcessOutput(TerminalEmulatorControl.Colorize($"Note: '{name}' in a compound command does not affect the next commands; type it on a line of its own.", 3) + "\r\n");
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
                        _environment.RemoveAll((KeyValuePair<string, string> item) => item.Key == assignment.Key);
                        _environment.Add(assignment);
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
    /// Opening the channel takes several round trips to the server and waits until the helper commands of the file
    /// panel have finished (see PtySetupGate), so it is done on a worker thread: on the UI thread the whole window used
    /// to freeze when a script was started from the file panel. The prompt stays hidden (no input line) until the
    /// channel is ready, so no second command can start meanwhile.
    /// </summary>
    private void RunRemoteCommand(string command)
    {
        SshSession session = _context.Session;
        // In byte mode (a shell counting UTF-8 bytes, OpenWrt) the PTY is opened without IUTF8, so that a program using
        // the kernel line editing (read, cat) counts bytes like BusyBox does and the repeated Backspace matches it.
        bool utf8Tty = !ByteOrientedLineEditing;
        ThreadPool.QueueUserWorkItem(delegate
        {
            SshShellChannel sshShellChannel = null;
            Exception failure = null;
            try
            {
                // The shell probe may be waited for here as well, which is not allowed on the UI thread either.
                sshShellChannel = session.OpenShellChannel(BuildRemoteCommand(command), "xterm", ExtendedDataMode.Merge, utf8Tty: utf8Tty);
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
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() => CompleteRemoteCommand(command, sshShellChannel, failure)));
                    posted = true;
                }
            }
            catch (InvalidOperationException)
            {
                // The tab is being closed.
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
            CommandRun item = new CommandRun(this, command, _currentDirectory, ((EditableTextBrowser)this).EditableDocument.LineCount - 1);
            _runs.Add(item);
            CommandChannel commandChannel = new CommandChannel(sshShellChannel, this, item);
            _channel = commandChannel;
            ResetByteColumns();
            sshShellChannel.Finished += (object sender) =>
            {
                // Read on the session thread: the exit status may still be on its way when the channel reports EOF.
                bool connectionLost = sshShellChannel.ClosedByConnectionLoss;
                bool disposed = sshShellChannel.IsDisposed;
                bool statusKnown = connectionLost || disposed ||
                    sshShellChannel.HasExitStatus || sshShellChannel.ExitSignal != null;
                int exitStatus = (connectionLost || disposed) ? 0 : sshShellChannel.ExitStatus;
                OnCommandFinished(commandChannel, connectionLost, exitStatus, statusKnown);
            };
            sshShellChannel.PollingEnabled = true;
            sshShellChannel.ResizePty(TerminalSize.Width, TerminalSize.Height);
        }
        catch (Exception exception)
        {
            ProcessOutput(TerminalEmulatorControl.Colorize(exception.Message, 1));
            if (_channel == null)
            {
                // Nothing runs: the channel was never attached, so it is closed here and the prompt comes back. If it was
                // attached, its Finished event shows the prompt.
                DisposeChannelQuietly(sshShellChannel);
                _lastCommandFailed = true;
                PrintLinePasteNote();
                ShowPrompt();
            }
        }
    }

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
    /// Pastes the clipboard text as commands that run one after another: the next one starts only when the previous one has
    /// ended, and the paste stops at the first command that fails (unless that is turned off in the settings) or when it
    /// is cancelled (Esc, or Cancel in the progress window). Returns false when nothing was started.
    /// </summary>
    public bool PasteLineByLine()
    {
        if (_inputLine == null || _linePaste != null)
        {
            ThemedMessageBox.Show("The terminal is busy with another command. Wait until it ends and try again.", "Paste line by line", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        try
        {
            // Every command runs when its turn comes, so the confirmation and the warnings of a paste apply.
            string text = PasteGuard.Prepare(ReadClipboardText(), AppSettings, linesRunAtOnce: true, owner: this);
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
            run.Progress = ThemedPasteProgress.Create("Paste line by line");
            run.Progress.CancelRequested += (object sender, EventArgs e) => CancelActivePaste();
            run.Progress.ShowNear(this);
            RunNextPastedLine();
            return true;
        }
        catch (Exception exception)
        {
            EndLinePaste();
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return false;
        }
    }

    // At a prompt: runs the next pasted command, or ends the paste.
    private void RunNextPastedLine()
    {
        LinePaste run = _linePaste;
        if (run == null || IsDisposed || _inputLine == null || _channel != null)
        {
            return;
        }
        if (run.Cancelled || run.Pending.Count == 0 || (run.StopOnError && _lastCommandFailed))
        {
            EndLinePaste();
            return;
        }
        string command = run.Pending.Dequeue();
        run.Started++;
        string shown = command.Length > 60 ? command.Substring(0, 57) + "..." : command;
        run.Progress?.SetProgress(run.Started - 1, run.Total, "Command " + run.Started + " of " + run.Total + ": " + shown);
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
        string note;
        if (run.Pending.Count == 0)
        {
            EndLinePaste();
            return;
        }
        if (run.Cancelled)
        {
            note = "[paste cancelled, " + run.Pending.Count + " command(s) not run]";
        }
        else if (run.StopOnError && _lastCommandFailed)
        {
            note = "[paste stopped after a failed command, " + run.Pending.Count + " command(s) not run]";
        }
        else
        {
            return;
        }
        EndLinePaste();
        ProcessOutput((CursorPosition.X > 0 ? "\r\n" : "") + TerminalEmulatorControl.Colorize(note, 3) + "\r\n");
    }

    protected override bool CancelActivePaste()
    {
        LinePaste run = _linePaste;
        if (run != null && !run.Cancelled)
        {
            run.Cancelled = true;
            run.Progress?.SetProgress(run.Started, run.Total, "Cancelling after the current command...");
            if (_inputLine != null)
            {
                EndLinePaste();
            }
            return true;
        }
        return base.CancelActivePaste();
    }

    internal override ILinkScanner CreateLinkScanner()
    {
        if (string.IsNullOrEmpty(VtSettings.AutoLinkExtensions))
        {
            return null;
        }
        return _channel?.Run?.CreateScanner();
    }

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
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(DrainUiQueue));
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // The tab is being closed: late output of its command has nowhere to go.
        }
        lock (_uiQueue)
        {
            _uiQueue.Clear();
        }
    }

    private void DrainUiQueue()
    {
        while (true)
        {
            Action action;
            lock (_uiQueue)
            {
                if (_uiQueue.Count == 0)
                {
                    break;
                }
                action = _uiQueue.Dequeue();
            }
            if (IsDisposed)
            {
                continue;
            }
            try
            {
                action();
            }
            catch
            {
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
            ResetLinkTracking();
            ResetByteColumns();
            _channel = null;
            _lastCommandFailed = connectionLost || (statusKnown && exitStatus != 0);
            string message = null;
            if (connectionLost)
            {
                message = TerminalEmulatorControl.Colorize("Connection lost.", 1);
            }
            else if (!(_context.VTSettings?.ShowExitCodes ?? true))
            {
                // Turned off in Smart Terminal Settings.
            }
            else if (!statusKnown)
            {
                message = TerminalEmulatorControl.Colorize("[exit status not reported]", 3);
            }
            else if (exitStatus > 0)
            {
                message = TerminalEmulatorControl.Colorize($"[exit code {exitStatus}]", 3);
            }
            else if (exitStatus < 0)
            {
                message = TerminalEmulatorControl.Colorize("[terminated by a signal]", 3);
            }
            if (message != null)
            {
                ProcessOutput((CursorPosition.X > 0 ? "\r\n" : "") + message + "\r\n");
            }
            PrintLinePasteNote();
            ShowPrompt();
        });
    }

    public override void PasteText(string clipboardText)
    {
        if (_inputLine == null)
        {
            base.PasteText(clipboardText);
            return;
        }
        try
        {
            // The command line is a single line: pasted lines are joined the way the shell would run them instead of
            // being glued together without a separator.
            // The text only lands in the command line and nothing runs until Enter, so it is cleaned and only a large
            // paste is confirmed.
            string text = PasteGuard.Prepare(PasteGuard.ApplyMode(clipboardText, PasteModeInUse), AppSettings, linesRunAtOnce: false, owner: this);
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
            InsertText(text);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Closing the tab stops the command that is running in it (tail -f, top, ...): its channel used to stay
            // open until the whole session was closed.
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
        base.Dispose(disposing);
    }
}
