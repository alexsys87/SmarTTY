using SmarTTY.Completion;
using SmarTTY.Controls;
using SmarTTY.Terminal;
using SmarTTY.Terminal.Emulation;
using SmarTTY.Terminal.Input;
using SmarTTY.Terminal.Rendering;
using SmarTTY.TextEditing;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The text terminal of a COM/TCP/telnet device on the WPF terminal (stage 2D): what <c>SerialTerminalControl</c> did, without WinForms.
/// When the device shows a prompt, the keys are edited here in an input line (<see cref="PromptLine"/>) that continues the device's own
/// prompt on its row (or, when there is none to continue, follows a local prompt on a line of its own): the history with Up/Down and
/// Ctrl+R, the grey suggestion from it, the completion of commands and of the device's paths, and the line is sent with Enter. While no
/// input line is shown (a command runs, a program of the device has the screen) the keys go to the device as in any terminal. The logic is
/// that of the WinForms control, moved as it was where it did not touch the editor.
/// </summary>
public sealed class WpfSerialTerminal : WpfTerminal, ILocalLinkHost, ILocalCompletionHost
{
    /// <summary>Data of <see cref="CommandSubmitted"/>.</summary>
    public sealed class CommandSubmittedEventArgs : EventArgs
    {
        /// <summary>The command as it is sent (trimmed).</summary>
        public string Command { get; }

        /// <summary>The line as it was typed, with leading blanks.</summary>
        public string RawText { get; }

        /// <summary>The prompt that was shown in front of the line, e.g. "login: " or "Password: ".</summary>
        public string Prompt { get; }

        public CommandSubmittedEventArgs(string command, string rawText, string prompt)
        {
            Command = command;
            RawText = rawText;
            Prompt = prompt;
        }
    }

    // The line being edited locally: after the device's own prompt (PromptOnScreen) or after a local one on a line of its own; null: the
    // keys go to the device.
    private PromptLine _inputLine;

    // What the input line and the full-screen state were when they were last reported, so a change is announced once.
    private bool _reportedHasInputLine;

    private bool _reportedFullScreenProgramActive;

    private readonly LineHistory _history = new LineHistory();

    private readonly HistorySuggestionState _suggestion = new HistorySuggestionState();

    private string _currentPrompt = "> ";

    private bool _autoDetectPrompt = true;

    private bool _localEcho = true;

    private bool _inputLineDisabled;

    // How many typed characters of the current line Backspace may still erase on the screen (EchoRawInput).
    private int _rawEchoLength;

    // Watches the escape sequences received while a command runs to tell a program that draws the whole screen from a shell at its prompt.
    private readonly RemoteProgramTracker _remoteProgram = new RemoteProgramTracker();

    // The size the device was told last (X < 0: not told on this connection yet).
    private GridPoint _announcedSize = new GridPoint(-1, -1);

    // The device has shown the prompt of a Linux shell ("# " or "$ ") and not another kind of prompt since.
    private bool _shellPromptSeen;

    private LocalCompletionSession _completionSession;

    private ICompletionPopup _completionPopup;

    // Set while a completion puts its text into the line: the session updates itself and must not see that as typing.
    private bool _applyingCompletion;

    private PathExistenceCache _pathExistence;

    private int _pathRedrawQueued;

    public WpfSerialTerminal()
    {
        // The links of the text a device printed are its addresses and the paths with a drive letter (LocalLinkScanner), opened by a plain click.
        Model.LinkScannerFactory = () => new LocalLinkScanner(this);
        View.LinksNeedControl = false;
        Model.StreamReset += OnStreamReset;
    }

    /// <summary>A click on an address or on a path with a drive letter shown in the text (see <c>SerialTerminalControl.OpenFileRequested</c>).</summary>
    public event EventHandler<OpenFileEventArgs> OpenFileRequested;

    public event EventHandler<CompletionPopupRequest> CompletionPopupRequested;

    /// <summary>Raised when the user submits a command line with Enter, before it is sent to the device.</summary>
    public event EventHandler<CommandSubmittedEventArgs> CommandSubmitted;

    /// <summary>Raised when <see cref="HasInputLine"/>, <see cref="FullScreenProgramActive"/> or the prompt changes.</summary>
    public event EventHandler InputLineStateChanged;

    /// <summary>Set by the device terminal: asks the shell of the device for the names that the path completion offers.</summary>
    public DeviceDirectoryListerHandler DeviceDirectoryLister { get; set; }

    /// <summary>Commands offered by Tab for the first word of a line besides the built-in ones: the command lists and the saved history.</summary>
    public IReadOnlyList<string> CommandSuggestions { get; set; } = new string[0];

    /// <summary>Put the command that sets the terminal size in front of the next command when the window has another size than the device was told.</summary>
    public bool SyncRemoteSize { get; set; }

    /// <summary>The characters typed while no input line is shown are also drawn by the terminal (for a device that does not echo).</summary>
    public bool EchoRawInput { get; set; }

    public bool LocalEcho
    {
        get => _localEcho;
        set => _localEcho = value;
    }

    /// <summary>True while the local input line is shown, i.e. the keys are edited here instead of being sent to the device.</summary>
    public bool HasInputLine => _inputLine != null;

    /// <summary>True while a program that draws the whole screen seems to run on the device and gets every key.</summary>
    public bool FullScreenProgramActive => _inputLine == null && _remoteProgram.OwnsScreen;

    public override bool EditsLocally => _inputLine != null;

    public string CurrentPrompt => _currentPrompt;

    public IReadOnlyList<string> CommandHistory => _history.Items;

    /// <summary>The number of columns and rows of the screen.</summary>
    public GridPoint TerminalSize => GridSize;

    /// <summary>True when the shell of the device can be asked for a listing now (see <c>SerialTerminalControl.CanListDeviceDirectory</c>).</summary>
    internal bool CanListDeviceDirectory => _inputLine != null && _shellPromptSeen && !_localEcho && !LineInputText.LooksLikeSecretPrompt(_inputLine.Prompt);

    /// <summary>When true, the input line is never shown and every key goes to the device at once; a shown one is taken off the screen.</summary>
    public bool InputLineDisabled
    {
        get => _inputLineDisabled;
        set
        {
            if (_inputLineDisabled == value)
            {
                return;
            }
            _inputLineDisabled = value;
            if (value)
            {
                DropInputLine();
            }
        }
    }

    /// <summary>True when "Send terminal size now" can run: the input line is shown and nothing is typed in it.</summary>
    public bool CanSendRemoteSize => _inputLine != null && _inputLine.Text.Length == 0 && LineInputText.BuildRemoteSizeCommand(GridSize.X, GridSize.Y) != null;

    /// <summary>True when <see cref="SubmitCommandNow"/> can run: the input line is shown and nothing is typed in it.</summary>
    public bool CanSubmitCommandNow => _inputLine != null && _inputLine.Text.Length == 0;

    // The local completion session and the link scanner ask the terminal for these.

    IEnumerable<CompletionItem> ILocalCompletionHost.GetSuggestedCommandItems()
    {
        foreach (string command in CommandSuggestions)
        {
            yield return new CompletionItem
            {
                Icon = VirtualTerminalPopupItemIcon.Command,
                Text = command,
                InsertText = command,
                Summary = new VirtualTerminalPopupSummary.OptionInfo
                {
                    Option = command,
                    Description = new CommandCompletion.OptionDescription { Summary = "Saved command" }
                }
            };
        }
    }

    void ILocalCompletionHost.Post(Action action)
    {
        if (!IsDisposed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
    }

    DeviceDirectoryListerHandler ILocalCompletionHost.DeviceDirectoryLister => DeviceDirectoryLister;

    bool ILocalCompletionHost.CanListDeviceDirectory => CanListDeviceDirectory;

    bool ILocalLinkHost.PathExists(string path)
    {
        return PathExistence.Exists(path);
    }

    void ILocalLinkHost.RequestOpenFile(string path)
    {
        RequestOpenFile(path);
    }

    public void RequestOpenFile(string path, int? line = null, int? column = null)
    {
        OpenFileRequested?.Invoke(this, new OpenFileEventArgs
        {
            Path = path,
            Line = line,
            Column = column
        });
    }

    // Whether the paths in the text exist: answered at once and checked on a thread of its own (a network drive that does not answer must
    // not freeze the window); the rows are drawn again when an answer changes.
    private PathExistenceCache PathExistence
    {
        get
        {
            if (_pathExistence == null)
            {
                _pathExistence = new PathExistenceCache(
                    path => File.Exists(path) || Directory.Exists(path),
                    work => new Thread(() => work()) { IsBackground = true, Name = "Local link path check" }.Start(),
                    OnPathExistenceChanged);
            }
            return _pathExistence;
        }
    }

    // On the thread of the check.
    private void OnPathExistenceChanged()
    {
        if (IsDisposed || Interlocked.Exchange(ref _pathRedrawQueued, 1) != 0)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _pathRedrawQueued, 0);
            if (!IsDisposed)
            {
                View.Invalidate();
            }
        }));
    }

    // The state of the input line.

    private void NotifyInputLineState()
    {
        if (_reportedHasInputLine == HasInputLine && _reportedFullScreenProgramActive == FullScreenProgramActive)
        {
            return;
        }
        _reportedHasInputLine = HasInputLine;
        _reportedFullScreenProgramActive = FullScreenProgramActive;
        InputLineStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private PromptLine CreateInputLine(int startLine, string prompt, bool promptOnScreen)
    {
        PromptLine line = new PromptLine(Model, startLine, prompt, promptOnScreen);
        line.TextChanged += OnInputTextChanged;
        line.CaretMoved += RefreshHistorySuggestion;
        return line;
    }

    // Keeps an open completion consistent with an edit of the line; closes it when the edit invalidated the word.
    private void OnInputTextChanged(int start, int removed, int added)
    {
        LocalCompletionSession session = _completionSession;
        if (session != null && !_applyingCompletion && _inputLine != null && session.OnTextChanged(_inputLine.Text, start, removed, added))
        {
            CloseCompletion();
        }
        RefreshHistorySuggestion();
    }

    /// <summary>Shows the input line again, whatever the terminal believes the device is doing (a program that ended without restoring the terminal).</summary>
    public void ReturnToInputLine()
    {
        _remoteProgram.Reset();
        if (_inputLine != null)
        {
            return;
        }
        Model.SwitchToNormalScreen();
        if (!ContinueDevicePrompt())
        {
            ShowPrompt();
        }
        NotifyInputLineState();
    }

    /// <summary>Takes the input line off the screen (the prompt of the device stays) and gives the keys to the device.</summary>
    private void DropInputLine()
    {
        PromptLine line = _inputLine;
        if (line == null)
        {
            return;
        }
        _history.EndSearch();
        line.Erase(keepLocalPrompt: false);
        GiveCursorBack(line);
        _inputLine = null;
        UpdateGhostText();
        NotifyInputLineState();
    }

    // The cursor of the screen goes back to the end of the device's prompt (or the start of the line of a local one), and the caret with it.
    private void GiveCursorBack(PromptLine line)
    {
        int row = line.StartLine - Model.ScreenTopLine;
        if (row >= 0 && row < Model.Rows)
        {
            Model.MoveCursorTo(new GridPoint(line.PromptOnScreen ? line.StartColumn : 0, row));
        }
        else
        {
            Model.SetPromptCaret(null);
        }
    }

    // True when the input line continues the device's own prompt and the device echoes what it receives: the device then draws the command.
    private bool ContinuesDevicePrompt => _inputLine != null && _inputLine.PromptOnScreen && !EchoRawInput;

    /// <summary>
    /// The device has just printed its prompt: the input line continues that very line after the device's cursor. False when the cursor is
    /// not at the end of a prompt (something follows it, or the text before it does not end like one).
    /// </summary>
    private bool ContinueDevicePrompt()
    {
        TerminalDisplayModel model = Model;
        if (_inputLineDisabled || model.ActiveBufferIndex != 0 || model.Columns <= 0)
        {
            return false;
        }
        EditableTextDocument document = model.Document;
        int line = model.CursorDocumentLine;
        int column = model.CursorPosition.X;
        if (line < 0 || line >= document.LineCount || column <= 0 || column >= model.Columns)
        {
            return false;
        }
        // A prompt is the last thing on the screen.
        string lineText = document.GetLineText(line);
        if (lineText.Length > column && !string.IsNullOrWhiteSpace(lineText.Substring(column)))
        {
            return false;
        }
        for (int next = line + 1; next < document.LineCount; next++)
        {
            if (!string.IsNullOrWhiteSpace(document.GetLineText(next)))
            {
                return false;
            }
        }
        // The blank after "# " is part of the prompt even where the line does not keep trailing blanks.
        string prompt = lineText.Length >= column ? lineText.Substring(0, column) : lineText.PadRight(column);
        if (!LineInputText.EndsLikePrompt(prompt, _currentPrompt))
        {
            return false;
        }
        _suggestion.Clear();
        // The lines below the prompt go (the line has nothing after it).
        _inputLine = CreateInputLine(line, prompt, promptOnScreen: true);
        _history.ResetNavigation();
        _history.EndSearch();
        UpdateGhostText();
        return true;
    }

    private void ShowPrompt()
    {
        if (_inputLineDisabled)
        {
            return;
        }
        TerminalDisplayModel model = Model;
        EditableTextDocument document = model.Document;
        // An empty last line is removed.
        if (document.LineCount > 0 && string.IsNullOrWhiteSpace(document.GetLineText(document.LineCount - 1)))
        {
            model.ReplaceDocumentTail(document.LineCount - 1, new string[0]);
        }
        _suggestion.Clear();
        _inputLine = CreateInputLine(document.LineCount, _currentPrompt, promptOnScreen: false);
        _history.ResetNavigation();
        _history.EndSearch();
        UpdateGhostText();
    }

    // Prompt detection and the size of the screen.

    /// <summary>Sends <paramref name="command"/> as a command of its own, ended with <paramref name="lineEnding"/> (not put in the history).</summary>
    public bool SubmitCommandNow(string command, string lineEnding)
    {
        if (!CanSubmitCommandNow || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        SubmitCommand(command, lineEnding);
        return true;
    }

    /// <summary>Sends the command that sets the terminal size as a command of its own (it is not put in the history).</summary>
    public bool SendRemoteSizeNow()
    {
        if (!CanSendRemoteSize)
        {
            return false;
        }
        GridPoint size = GridSize;
        _announcedSize = size;
        SubmitCommand(LineInputText.BuildRemoteSizeCommand(size.X, size.Y));
        return true;
    }

    // The terminal size command with "; " after it for the command about to be sent, or nothing.
    private string TakeRemoteSizePrefix(string command, string prompt)
    {
        if (!SyncRemoteSize || !_shellPromptSeen || command.Length == 0 || LineInputText.LooksLikeSecretPrompt(prompt))
        {
            return string.Empty;
        }
        GridPoint size = GridSize;
        string sizeCommand = LineInputText.BuildRemoteSizeCommand(size.X, size.Y);
        if (sizeCommand == null || (size.X == _announcedSize.X && size.Y == _announcedSize.Y))
        {
            return string.Empty;
        }
        _announcedSize = size;
        return sizeCommand + "; ";
    }

    private void TrackShellPrompt(string text)
    {
        if (LineInputText.EndsLikeShellPrompt(text))
        {
            _shellPromptSeen = true;
        }
        else if (LineInputText.EndsLikePrompt(text, null))
        {
            _shellPromptSeen = false;
        }
    }

    public void SetPrompt(string prompt, bool autoDetect = true)
    {
        _currentPrompt = prompt ?? "> ";
        _autoDetectPrompt = autoDetect;
    }

    public void EnableAutoPromptDetection(bool enable)
    {
        _autoDetectPrompt = enable;
    }

    private void DetectPrompt(string text)
    {
        // What the shell sends after its prompt without printing anything (BusyBox ash asks for the cursor position) is not the prompt.
        text = LineInputText.TrimTrailingControlSequences(text);
        string[] promptPatterns = { "> ", "# ", "$ ", ">> ", "] ", "-> " };
        foreach (string pattern in promptPatterns)
        {
            int lastIndex = text.LastIndexOf(pattern, StringComparison.Ordinal);
            if (lastIndex >= 0 && lastIndex + pattern.Length == text.Length)
            {
                _currentPrompt = pattern;
                return;
            }
        }
    }

    // The history.

    public void ClearHistory()
    {
        _history.Clear();
    }

    /// <summary>The history for the "Command history" window: every command once, the newest first.</summary>
    public List<string> GetCommandHistory()
    {
        return _history.Distinct();
    }

    /// <summary>Removes <paramref name="commands"/> from the history (Up/Down, Ctrl+R and the grey suggestion).</summary>
    public void RemoveFromHistory(IEnumerable<string> commands)
    {
        if (_history.Remove(commands))
        {
            RefreshHistorySuggestion();
        }
    }

    public void AddToHistory(string command)
    {
        _history.Add(command);
    }

    /// <summary>Puts a command from the history window into the input line in place of what is typed there; without one it goes to the device as typed.</summary>
    public void PutCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }
        if (_inputLine == null)
        {
            base.InsertCommand(command);
            return;
        }
        _history.EndSearch();
        _history.ResetNavigation();
        CloseCompletion();
        _inputLine.SetText(command);
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    // Ctrl+R: the input line shows the newest command that contains what was typed, the next press the one before.
    private void HandleHistorySearch()
    {
        string match = _history.NextSearchMatch(_inputLine.Text);
        if (match != null)
        {
            _inputLine.SetText(match);
        }
    }

    private void CancelHistorySearch()
    {
        if (_history.IsSearching && _inputLine != null)
        {
            string original = _history.SearchOriginalInput;
            _history.EndSearch();
            _inputLine.SetText(original);
        }
    }

    // Up (forward == false) and Down: on a line with text only the commands that start with it, unless that is turned off.
    private void NavigateHistory(bool forward)
    {
        if (_inputLine == null || _history.Count == 0)
        {
            return;
        }
        string shown = _history.Navigate(_inputLine.Text, older: !forward, HistoryPrefixSearchEnabled);
        if (shown == null)
        {
            return;
        }
        _inputLine.SetText(shown);
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    private AutoCompletionTriggers CompletionTriggers => AppSettings?.VirtualTerminal?.CompletionTriggers;

    private bool HistorySuggestionsEnabled => CompletionTriggers?.HistorySuggestions ?? true;

    private bool HistoryPrefixSearchEnabled => CompletionTriggers?.HistoryPrefixSearch ?? true;

    private string ComputeHistorySuggestion()
    {
        PromptLine line = _inputLine;
        if (line == null)
        {
            return null;
        }
        string text = line.Text;
        return _suggestion.Compute(true, _inputLineDisabled, Model.HasSelection, Model.Columns, HistorySuggestionsEnabled,
            LineInputText.LooksLikeSecretPrompt(line.Prompt), text, () => line.CaretIndex == text.Length, _history.NewestFirst);
    }

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
        PromptLine line = _inputLine;
        string suggestion = _suggestion.Current;
        if (line == null || suggestion == null || Model.HasSelection)
        {
            View.SetGhostText(default(TextDocument.TextPosition), null);
            return;
        }
        View.SetGhostText(line.PositionAt(line.Text.Length), suggestion);
    }

    /// <summary>Right or End at the end of the line take the whole suggestion, Ctrl+Right its next word; false when none is shown.</summary>
    private bool TryAcceptHistorySuggestion(VirtualKey key, bool shift, bool control, bool alt)
    {
        PromptLine line = _inputLine;
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
        _history.EndSearch();
        line.Replace(line.Text.Length, 0, part);
        // As if the characters were typed.
        if (_localEcho)
        {
            SendInput(part, TerminalInputKind.Typed);
        }
        return true;
    }

    // The completion.

    private void ShowCompletions()
    {
        PromptLine line = _inputLine;
        if (line == null)
        {
            return;
        }
        if (_completionSession != null)
        {
            CloseCompletion();
        }
        string text = line.Text;
        int caretIndex = line.CaretIndex;
        CompletionSession.Token[] tokens = CompletionSession.Tokenize(text);
        int? matchIndex = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Contains(caretIndex))
            {
                matchIndex = i;
                break;
            }
        }
        CompletionTarget target;
        int tokenStart;
        int tokenEnd;
        if (matchIndex.HasValue)
        {
            CompletionSession.Token token = tokens[matchIndex.Value];
            tokenStart = token.Start;
            tokenEnd = token.End;
            if (matchIndex == 0)
            {
                target = CompletionTarget.Command;
            }
            else
            {
                string previousText = tokens[matchIndex.Value - 1].Extract(text);
                target = IsDirectoryCommand(previousText) ? CompletionTarget.Directory : CompletionTarget.Path;
            }
        }
        else
        {
            // The caret is between words or after the last one ("rm |"): an empty word at the caret is completed.
            tokenStart = caretIndex;
            tokenEnd = caretIndex;
            CompletionSession.Token? previous = null;
            foreach (CompletionSession.Token token in tokens)
            {
                if (token.End < caretIndex)
                {
                    previous = token;
                }
            }
            target = previous.HasValue
                ? (IsDirectoryCommand(previous.Value.Extract(text)) ? CompletionTarget.Directory : CompletionTarget.Path)
                : CompletionTarget.Command;
        }
        GetCaretScreenPlace(out GridPoint location, out int lineHeight);
        CompletionPopupRequest request = new CompletionPopupRequest
        {
            ScreenLocation = location,
            LineHeight = lineHeight,
            Items = null,
            StatusText = "Loading suggestions...",
            Title = target == CompletionTarget.Command ? "Commands" : (target == CompletionTarget.Directory ? "Directories" : "Files"),
            Icon = target == CompletionTarget.Command ? VirtualTerminalPopupIcon.Command : VirtualTerminalPopupIcon.Directory
        };
        CompletionPopupRequested?.Invoke(this, request);
        if (request.Popup != null)
        {
            _completionPopup = request.Popup;
            _completionSession = new LocalCompletionSession(this, text, target, tokenStart, tokenEnd, _completionPopup, true);
            _completionSession.ItemApplied += OnCompletionApplied;
        }
    }

    private static bool IsDirectoryCommand(string word)
    {
        return word == "cd" || word == "ls" || word == "dir";
    }

    private void OnCompletionApplied(object sender, CompletionSession.AppliedEventArgs args)
    {
        PromptLine line = _inputLine;
        if (line != null)
        {
            _applyingCompletion = true;
            try
            {
                // The caret index of the session is an index of the text (the WinForms control took the length of the prompt off it).
                line.SetText(args.NewText, args.CaretIndex);
            }
            finally
            {
                _applyingCompletion = false;
            }
        }
        if (args.Commit)
        {
            SubmitCommand();
        }
        else if (args.IsDirectory)
        {
            // Go on with the contents of the directory.
            ShowCompletions();
        }
        else
        {
            // A completed name cannot keep listing: the list must not show stale names while the user keeps typing.
            CloseCompletion();
        }
    }

    private void CloseCompletion()
    {
        _completionSession?.Dispose();
        _completionSession = null;
        _completionPopup = null;
    }

    // Sending.

    private void SendLineToDevice()
    {
        if (_inputLine != null)
        {
            // Only what was typed: the prompt is the device's own text (or a local marker), not input.
            SendInput(_inputLine.Text, TerminalInputKind.Typed);
        }
    }

    private void SubmitCommand()
    {
        SubmitCommand(null);
    }

    // commandOverride: a command made by the terminal itself (the terminal size), sent instead of the text of the input line; it is not put
    // in the history and not reported as typed. lineEndingOverride: what ends the command instead of the Enter of the connection.
    private void SubmitCommand(string commandOverride, string lineEndingOverride = null)
    {
        PromptLine line = _inputLine;
        if (line == null)
        {
            return;
        }
        bool typedByUser = commandOverride == null;
        string command = (commandOverride ?? line.Text).Trim();
        _history.EndSearch();
        CloseCompletion();
        if (typedByUser)
        {
            // What is typed at a "Password:" prompt is not kept in the history.
            if (!LineInputText.LooksLikeSecretPrompt(line.Prompt))
            {
                AddToHistory(command);
            }
            CommandSubmitted?.Invoke(this, new CommandSubmittedEventArgs(command, line.Text, line.Prompt));
        }
        if (ContinuesDevicePrompt)
        {
            // The device echoes the command right after its prompt, where its cursor still is: the locally typed copy is taken away so the
            // command is not shown twice.
            line.Erase(keepLocalPrompt: false);
            GiveCursorBack(line);
        }
        else
        {
            // The output starts on the next line.
            Model.BeginOutputAtEnd();
        }
        string lineEnding = lineEndingOverride ?? EnterSequence ?? "\r";
        string sizePrefix = typedByUser ? TakeRemoteSizePrefix(command, line.Prompt) : string.Empty;
        SendInput(sizePrefix + command + lineEnding, TerminalInputKind.Typed);
        _inputLine = null;
        UpdateGhostText();
        // Whatever ran before is over; the command just sent starts with a clean slate.
        _remoteProgram.Reset();
        NotifyInputLineState();
    }

    private void ClearCurrentLine()
    {
        PromptLine line = _inputLine;
        _history.EndSearch();
        _history.ResetNavigation();
        CloseCompletion();
        if (ContinuesDevicePrompt)
        {
            // The shell answers Ctrl+C itself with "^C" and a new prompt, which the input line then continues.
            line.Erase(keepLocalPrompt: false);
            Model.SetPromptCaret(null);
            _inputLine = null;
            UpdateGhostText();
            SendInput("\u0003", TerminalInputKind.KeySequence);
            return;
        }
        line.SetText("");
        ProcessOutput("^C");
        SendInput("\u0003", TerminalInputKind.KeySequence);
        ShowPrompt();
    }

    /// <summary>
    /// Clears the screen for the person (the Clear button, Ctrl+L): when a command was being typed, its prompt and text are drawn again at
    /// the top, so typing goes on. Nothing is sent to the device.
    /// </summary>
    public void ClearScreen()
    {
        PromptLine previous = _inputLine;
        Reset();
        if (previous == null)
        {
            return;
        }
        if (previous.PromptOnScreen)
        {
            ProcessOutput(previous.Prompt);
            if (!ContinueDevicePrompt())
            {
                ShowPrompt();
            }
        }
        else
        {
            ShowPrompt();
        }
        if (_inputLine != null && previous.Text.Length > 0)
        {
            _inputLine.SetText(previous.Text);
        }
        NotifyInputLineState();
    }

    public override void ClearOutput()
    {
        ClearScreen();
    }

    private void ClearScreenAndRedrawPrompt()
    {
        _history.EndSearch();
        ClearScreen();
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    // No input line until the device shows its prompt: the input line then continues it.
    public override void BeginNewSession()
    {
        base.BeginNewSession();
        ForgetInputLine();
        _announcedSize = new GridPoint(-1, -1);
        _shellPromptSeen = false;
        NotifyInputLineState();
    }

    public override void Reset()
    {
        base.Reset();
        ForgetInputLine();
        NotifyInputLineState();
    }

    // ESC c from the device, which prints its prompt again by itself (the model has done the reset).
    private void OnStreamReset()
    {
        ForgetInputLine();
        NotifyInputLineState();
    }

    private void ForgetInputLine()
    {
        CloseCompletion();
        _inputLine = null;
        Model.SetPromptCaret(null);
        _remoteProgram.Reset();
        _history.EndSearch();
        _history.ResetNavigation();
        UpdateGhostText();
    }

    /// <summary>Shows text that was not received from the device (what was sent), on a line of its own.</summary>
    public void ShowLocalText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (Model.CursorPosition.X != 0)
        {
            text = "\r\n" + text;
        }
        ProcessLocalOutput(text);
    }

    private void ProcessLocalOutput(string text)
    {
        View.FlushOutput();
        Model.ProcessLocalOutput(text);
    }

    // The keys.

    private static void GetKey(KeyEventArgs e, out VirtualKey key, out bool shift, out bool control, out bool alt)
    {
        Key wpfKey = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
        shift = (modifiers & ModifierKeys.Shift) != 0;
        control = (modifiers & ModifierKeys.Control) != 0;
        alt = (modifiers & ModifierKeys.Alt) != 0;
        key = wpfKey == Key.ImeProcessed || wpfKey == Key.DeadCharProcessed ? VirtualKey.None : (VirtualKey)KeyInterop.VirtualKeyFromKey(wpfKey);
    }

    // What SerialTerminalControl.OnKeyDown did before the device panel saw the key.
    protected override void OnKeyBeforeShortcuts(KeyEventArgs e)
    {
        GetKey(e, out VirtualKey key, out bool shift, out bool control, out bool alt);
        if (key == VirtualKey.None)
        {
            return;
        }
        PromptLine line = _inputLine;
        if (line == null)
        {
            if (EchoRawInput && !control && !alt)
            {
                if (key == VirtualKey.Return)
                {
                    ProcessLocalOutput("\r\n");
                    _rawEchoLength = 0;
                }
                else if (key == VirtualKey.Back)
                {
                    // After the key was sent: in byte mode the number of DELs depends on the letter still shown before the cursor.
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        if (_rawEchoLength > 0 && _inputLine == null && !IsDisposed)
                        {
                            ProcessLocalOutput("\b \b");
                            _rawEchoLength--;
                        }
                    }));
                }
            }
            return;
        }
        KeyStroke stroke = new KeyStroke(key | (shift ? VirtualKey.Shift : 0) | (control ? VirtualKey.Control : 0) | (alt ? VirtualKey.Alt : 0));
        if (_completionPopup != null && _completionPopup.TryHandleKeyEvent(stroke))
        {
            e.Handled = true;
            return;
        }
        if (TryAcceptHistorySuggestion(key, shift, control, alt))
        {
            e.Handled = true;
            return;
        }
        bool plain = !control && !shift && !alt;
        bool controlOnly = control && !shift && !alt;
        if ((key == VirtualKey.Tab && plain) || (key == VirtualKey.Space && controlOnly))
        {
            ShowCompletions();
            e.Handled = true;
            return;
        }
        if (key == VirtualKey.R && controlOnly)
        {
            HandleHistorySearch();
            e.Handled = true;
            return;
        }
        if ((key == VirtualKey.Up || key == VirtualKey.Down) && plain)
        {
            NavigateHistory(forward: key == VirtualKey.Down);
            e.Handled = true;
            return;
        }
        if (key == VirtualKey.Escape)
        {
            if (_history.IsSearching)
            {
                CancelHistorySearch();
                e.Handled = true;
                return;
            }
            if (_completionSession != null)
            {
                CloseCompletion();
                e.Handled = true;
                return;
            }
            if (_suggestion.Current != null)
            {
                _suggestion.Dismiss(line.Text);
                RefreshHistorySuggestion();
                e.Handled = true;
                return;
            }
        }
        if (key == VirtualKey.Return && plain)
        {
            SubmitCommand();
            e.Handled = true;
            return;
        }
        if (key == VirtualKey.C && controlOnly)
        {
            ClearCurrentLine();
            e.Handled = true;
            return;
        }
        if (key == VirtualKey.L && controlOnly)
        {
            ClearScreenAndRedrawPrompt();
            e.Handled = true;
        }
    }

    // The editing keys of the input line, after the shortcuts of the panel (the keys of the editor of the WinForms control). Nothing that
    // the line does not use goes to the device while it is shown.
    protected override void OnTerminalKey(KeyEventArgs e)
    {
        PromptLine line = _inputLine;
        if (line == null)
        {
            return;
        }
        GetKey(e, out VirtualKey key, out bool shift, out bool control, out bool alt);
        if (key == VirtualKey.None || IsModifierKey(key))
        {
            // A dead key or the input method: the character comes as text input.
            return;
        }
        if (!alt)
        {
            switch (key)
            {
                case VirtualKey.Left:
                case VirtualKey.Right:
                    _history.EndSearch();
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
                    EditLine(() => line.DeleteBackward(control));
                    e.Handled = true;
                    return;
                case VirtualKey.Delete when !shift:
                    EditLine(() => line.DeleteForward(control));
                    e.Handled = true;
                    return;
                case VirtualKey.A when control && !shift:
                    line.SelectAll();
                    UpdateGhostText();
                    e.Handled = true;
                    return;
                case VirtualKey.PageUp:
                case VirtualKey.PageDown:
                    if (!shift && !control)
                    {
                        Model.TopLine += key == VirtualKey.PageUp ? -Model.Rows : Model.Rows;
                        e.Handled = true;
                    }
                    return;
            }
        }
        // The actions of the view: the paste and copy keys, Shift+PageUp/Down.
        if ((key == VirtualKey.Insert && (shift || control)) || (control && shift && (key == VirtualKey.C || key == VirtualKey.V)))
        {
            return;
        }
        // AltGr types the characters of the layout (as text input).
        bool altGr = control && alt && Keyboard.IsKeyDown(Key.RightAlt);
        if (altGr || (!control && !alt && TypesText(key)))
        {
            return;
        }
        e.Handled = true;
    }

    private void EditLine(Action edit)
    {
        _history.EndSearch();
        if (Model.HasSelection && !_inputLine.HasSelection)
        {
            // Typing goes to the input line even when text above it is selected (e.g. to copy it).
            Model.ClearSelection();
        }
        edit();
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
            case VirtualKey.NumLock:
                return true;
        }
        return false;
    }

    // A key that a keyboard layout turns into a character, which then comes as text input.
    private static bool TypesText(VirtualKey key)
    {
        return (key >= VirtualKey.D0 && key <= VirtualKey.D9) || (key >= VirtualKey.A && key <= VirtualKey.Z)
            || (key >= VirtualKey.NumPad0 && key <= VirtualKey.Divide) || (key >= VirtualKey.OemSemicolon && key <= VirtualKey.Oemtilde)
            || (key >= VirtualKey.OemOpenBrackets && key <= VirtualKey.Oem8) || key == VirtualKey.OemBackslash || key == VirtualKey.Space
            || key == VirtualKey.Packet;
    }

    // What SerialTerminalControl.OnKeyPress did.
    protected override bool OnTerminalText(TextCompositionEventArgs e)
    {
        string text = e.Text;
        PromptLine line = _inputLine;
        if (line == null)
        {
            if (EchoRawInput && !string.IsNullOrEmpty(text))
            {
                foreach (char ch in text)
                {
                    if (!char.IsControl(ch))
                    {
                        _rawEchoLength++;
                        ProcessLocalOutput(ch.ToString());
                    }
                }
            }
            // The terminal sends it.
            return false;
        }
        if (string.IsNullOrEmpty(text))
        {
            // Alt with a character, Ctrl with a key the line does not use: nothing.
            return true;
        }
        foreach (char ch in text)
        {
            TypeCharacter(ch);
            if (_inputLine == null)
            {
                break;
            }
        }
        return true;
    }

    private void TypeCharacter(char ch)
    {
        PromptLine line = _inputLine;
        if (ch == '\b')
        {
            EditLine(() => line.DeleteBackward(word: false));
            return;
        }
        if (ch == '\r' || ch == '\n')
        {
            SubmitCommand();
            return;
        }
        if (char.IsControl(ch))
        {
            return;
        }
        EditLine(() => line.Insert(ch.ToString()));
        if (_localEcho)
        {
            SendInput(ch.ToString(), TerminalInputKind.Typed);
        }
        // A blank or a slash (for paths) opens the completion.
        if ((ch == ' ' || ch == '/') && _completionPopup == null)
        {
            ShowCompletions();
        }
    }

    protected override void OnDocumentClicked(TextDocument.TextPosition position)
    {
        if (_inputLine != null && _inputLine.Contains(position))
        {
            _inputLine.MoveCaret(_inputLine.IndexOf(position), extend: false);
        }
    }

    protected override void OnGridResized(int columns, int rows)
    {
        // The input line is wrapped at the new width (the caret keeps its index).
        _inputLine?.Render();
        UpdateGhostText();
    }

    public override void SelectAll()
    {
        if (_inputLine != null)
        {
            _inputLine.SelectAll();
            UpdateGhostText();
            return;
        }
        base.SelectAll();
    }

    // Commands and pastes.

    /// <summary>
    /// Tools > Quick commands: the command goes into the input line at the caret and is not sent; while a program of the device has the keys
    /// it is typed to the device instead.
    /// </summary>
    public override void InsertCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (_inputLine == null)
        {
            base.InsertCommand(text);
            return;
        }
        _history.EndSearch();
        EditLine(() => _inputLine.Insert(text));
        if (_localEcho)
        {
            SendInput(text, TerminalInputKind.Paste);
        }
    }

    public override void PasteText(string clipboardText)
    {
        if (_inputLine == null)
        {
            base.PasteText(clipboardText);
            return;
        }
        PasteIntoInputLine(clipboardText, PasteModeInUse);
    }

    /// <summary>
    /// Pastes into the input line the way the smart terminal does: the text goes where the caret is (a selection of the line is replaced),
    /// several lines become one command line as a shell would run them, and nothing is sent before Enter. False when no input line is shown.
    /// </summary>
    public bool PasteIntoInputLine(string clipboardText, PasteMode mode)
    {
        if (_inputLine == null)
        {
            return false;
        }
        try
        {
            string text = PasteGuard.Prepare(PasteGuard.ApplyMode(clipboardText, mode), AppSettings, linesRunAtOnce: false, owner: PasteAnchor);
            text = ShellCommandLine.JoinPastedLines(text);
            // The device may use an encoding without some of the characters of the text.
            text = !string.IsNullOrEmpty(text) ? PasteGuard.FitToEncoding(text, InputEncoding, AppSettings, PasteAnchor) : null;
            if (_inputLine == null || string.IsNullOrEmpty(text))
            {
                return true;
            }
            _history.EndSearch();
            EditLine(() => _inputLine.Insert(text));
            if (_localEcho)
            {
                SendInput(text, TerminalInputKind.Paste);
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        return true;
    }

    // The output of the device.

    /// <summary>Shows what the device sent (on the UI thread) and shows, keeps or drops the input line by what it ends with.</summary>
    public void OnDataReceived(string text)
    {
        try
        {
            OnDataReceivedCore(text);
        }
        finally
        {
            NotifyInputLineState();
        }
    }

    private void OnDataReceivedCore(string text)
    {
        if (ContinuesDevicePrompt && !string.IsNullOrEmpty(text) && LineInputText.TrimTrailingControlSequences(text).Length > 0)
        {
            // The device prints something while a command is being typed after its prompt: it would be written over the typed text.
            PrintAboveInputLine(text);
            return;
        }
        ProcessOutput(text);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (_inputLine != null)
        {
            // A shell prompt that arrives while the input line is shown still counts.
            TrackShellPrompt(text);
            return;
        }
        // A program that draws the whole screen (mc, htop, vi ...) keeps the keyboard however much of its output looks like a prompt.
        _remoteProgram.Observe(text, Model.ActiveBufferIndex != 0);
        if (_remoteProgram.OwnsScreen)
        {
            return;
        }
        TrackShellPrompt(text);
        if (_autoDetectPrompt)
        {
            DetectPrompt(text);
        }
        // Only line breaks are trimmed: the blank after "> ", "# ", "$ " is part of the pattern.
        if (LineInputText.EndsLikePrompt(text, _currentPrompt))
        {
            if (!ContinueDevicePrompt())
            {
                ShowPrompt();
            }
        }
    }

    /// <summary>
    /// Shows text that the device printed while a command was being typed after its prompt: the typed text is taken off the screen, the
    /// device's text is printed, and the input line comes back with what was typed and the caret where it was, after the device's new prompt
    /// or after a copy of the old one on the next line.
    /// </summary>
    private void PrintAboveInputLine(string text)
    {
        PromptLine previous = _inputLine;
        previous.Erase(keepLocalPrompt: false);
        GiveCursorBack(previous);
        _inputLine = null;
        ProcessOutput(text);
        TrackShellPrompt(text);
        bool continued = LineInputText.EndsLikePrompt(text, _currentPrompt) && ContinueDevicePrompt();
        if (!continued)
        {
            if (Model.CursorPosition.X != 0)
            {
                ProcessOutput("\r\n");
            }
            ProcessOutput(previous.Prompt);
            if (!ContinueDevicePrompt())
            {
                ShowPrompt();
            }
        }
        if (_inputLine != null && previous.Text.Length > 0)
        {
            _inputLine.SetText(previous.Text, previous.CaretIndex);
        }
        UpdateGhostText();
    }

    protected override void OnDisposing()
    {
        try
        {
            CloseCompletion();
        }
        catch
        {
        }
    }
}
