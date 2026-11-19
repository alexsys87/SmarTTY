using SmarTTY.Terminal.Emulation;
using System.ComponentModel;
using SmarTTY.Forms;
using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using VisualTTY;
using VisualTTY.DeviceTerminal;

// The file uses C# 8 nullable reference type annotations (e.g. InputLine?,
// string?) but the net48 project has no	#nullable context enabled; enable the
// annotations context only so those '?' markers are valid without turning on
// nullable warnings project-wide.
#nullable enable annotations

namespace SmarTTY.Controls;

public class SerialTerminalControl : TerminalEmulatorControl, ILocalLinkHost
{
    // The line being edited locally. Its prompt is either the device's own prompt, already on the screen (the input
    // continues that line: StartColumn is the end of the prompt), or a local prompt drawn on a line of its own
    // (StartColumn 0) when no device prompt is there to continue.
    private class InputLine
    {
        public readonly int StartLine;
        public readonly int StartColumn;
        public readonly string Prompt;
        private string _text = "";
        private int _caretIndex = 0;

        public string Text => _text;
        public int CaretIndex => _caretIndex;
        public TextDocument.TextPosition StartPosition => new TextDocument.TextPosition(StartColumn, StartLine);

        public InputLine(int startLine, int startColumn, string prompt)
        {
            StartLine = startLine;
            StartColumn = startColumn;
            Prompt = prompt;
        }

        public void SetText(string text)
        {
            _text = text ?? "";
            _caretIndex = _text.Length;
        }

        public void SetCaretIndex(int index)
        {
            _caretIndex = index < 0 ? 0 : (index > _text.Length ? _text.Length : index);
        }
    }

    private InputLine? _inputLine;

    // What the input line and the full-screen state were when they were last reported, so a change is announced
    // once (the status bar shows them).
    private bool _reportedHasInputLine;

    private bool _reportedFullScreenProgramActive;
    private readonly LineHistory _history = new LineHistory();

    // The grey rest of a previously typed command drawn after the caret (see HistorySuggestion), and what hides it.
    private readonly HistorySuggestionState _suggestion = new HistorySuggestionState();
    private string _currentPrompt = "> ";
    private bool _autoDetectPrompt = true;
    private bool _localEcho = true;

    /// <summary>
    /// Raised when the user clicks an address or a path with a drive letter shown in the terminal text (see LocalLinkRules.IsLocalPath:
    /// "/tmp" or "build.log" of the device is no link). The controller opens an address in the browser; a path of this machine only
    /// after a question, since the device may be another Windows computer with its own C:\, and then a folder in Explorer, a document
    /// with its program and anything else as text (see LocalLinkPolicy): the path is what the device printed, and a file must not be run for it.
    /// </summary>
    public event EventHandler<OpenFileEventArgs> OpenFileRequested;

    public void RequestOpenFile(string path, int? line = null, int? column = null)
    {
        OpenFileRequested?.Invoke(this, new OpenFileEventArgs
        {
            Path = path,
            Line = line,
            Column = column
        });
    }

    private SmarTTY.Utilities.PathExistenceCache _pathExistence;

    private int _pathRedrawQueued;

    /// <summary>
    /// Whether the paths in the text exist, for painting and hovering: it answers at once and checks in the background
    /// (File.Exists on a network drive that does not answer, or on a sleeping disk, would freeze the window). The check has
    /// a thread of its own, so a drive that hangs does not hold a thread of the pool.
    /// </summary>
    private SmarTTY.Utilities.PathExistenceCache PathExistence
    {
        get
        {
            if (_pathExistence == null)
            {
                _pathExistence = new SmarTTY.Utilities.PathExistenceCache(
                    path => File.Exists(path) || Directory.Exists(path),
                    work => new System.Threading.Thread(() => work()) { IsBackground = true, Name = "Local link path check" }.Start(),
                    OnPathExistenceChanged);
            }
            return _pathExistence;
        }
    }

    // Called on the thread of the check when a path turned out to exist or not (the underline of a link depends on it).
    private void OnPathExistenceChanged()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        // One request is enough for a burst of results.
        if (System.Threading.Interlocked.Exchange(ref _pathRedrawQueued, 1) != 0)
        {
            return;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                System.Threading.Interlocked.Exchange(ref _pathRedrawQueued, 0);
                if (!IsDisposed)
                {
                    Redraw();
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            // The window went away in the meantime.
            System.Threading.Interlocked.Exchange(ref _pathRedrawQueued, 0);
        }
    }

    // Completion infrastructure
    private enum CompletionTarget
    {
        Command,
        Path,
        Directory
    }

    private class LocalCompletionSession : IDisposable
    {
        private readonly SerialTerminalControl _terminal;
        private readonly CompletionTarget _target;
        private string _commandText;
        private int _tokenStart;
        private int _tokenEnd;
        private readonly ICompletionPopup _popup;
        private readonly bool _autoApply;
        private List<CompletionItem> _items;
        // Directory whose children _items was last listed from ("" for command targets).
        private string _itemsDirectory = "";

        public event EventHandler<CompletionSession.AppliedEventArgs> ItemApplied;

        public LocalCompletionSession(SerialTerminalControl terminal, string commandText, CompletionTarget target, int tokenStart, int tokenEnd, ICompletionPopup popup, bool autoApply)
        {
            _terminal = terminal;
            _target = target;
            _commandText = commandText;
            _tokenStart = tokenStart;
            _tokenEnd = tokenEnd;
            _popup = popup;
            _autoApply = autoApply;
            _popup.ItemSelected += OnItemSelected;
            LoadItems();
            UpdatePopup();
        }

        private string GetTokenText()
        {
            if (_tokenStart >= 0 && _tokenEnd <= _commandText.Length && _tokenStart <= _tokenEnd)
            {
                return _commandText.Substring(_tokenStart, _tokenEnd - _tokenStart);
            }
            return "";
        }

        private void LoadItems()
        {
            try
            {
                List<CompletionItem> itemsList = new List<CompletionItem>();
                string tokenText = GetTokenText();
                string filterText = tokenText.Trim('\'', '"');

                if (_target == CompletionTarget.Command)
                {
                    // The user's own commands and the saved history, matched by what is already typed.
                    itemsList.AddRange(_terminal.GetSuggestedCommandItems());
                }

                if (_target == CompletionTarget.Command && string.IsNullOrEmpty(filterText))
                {
                    // Built-in local commands
                    _itemsDirectory = "";
                    itemsList.AddRange(LocalCompletionItems.GetBuiltInCommands());
                    // Local executables from PATH
                    itemsList.AddRange(LocalCompletionItems.GetLocalExecutables(filterText));
                }
                else if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
                {
                    // The files are on the device: its shell lists them (the folder of this program used to be offered).
                    _itemsDirectory = LocalCompletionItems.GetDeviceDirectoryPrefix(tokenText);
                    _items = itemsList;
                    RequestDeviceListing(_itemsDirectory);
                    return;
                }

                _items = itemsList;
                _terminal.BeginInvoke((Action)(() => OnItemsLoaded()));
            }
            catch (Exception ex)
            {
                _popup.DisplayException(ex);
            }
        }

        // Live-sync: called by the terminal after every text edit that lands
        // inside this session's token. Keeps the token text, the item listing
        // and the popup filter consistent with what the user is typing.
        // Returns true when the edit invalidated the completion token (the
        // session must be closed, e.g. the token turned into a region of shell
        // metacharacters or an unescaped space that listing cannot represent).
        public bool OnTextChanged(string newFullText, int offset, int oldLength, int newLength)
        {
            if (_popup == null)
            {
                return true;
            }

            int endOffset = offset + oldLength;
            if (offset < _tokenStart || offset > _tokenEnd || endOffset > _tokenEnd)
            {
                // Edit happened outside the completed token - the session is stale.
                return true;
            }

            _tokenEnd += newLength - oldLength;
            _commandText = newFullText;

            string extracted = GetTokenText();
            if (string.IsNullOrEmpty(extracted))
            {
                UpdatePopup();
                return false;
            }

            if (extracted.IndexOfAny(LocalCompletionItems.CompletionMetaChars) != -1)
            {
                return true;
            }
            if (LocalCompletionItems.ContainsUnescapedSpace(extracted) && extracted[0] != '\'' && extracted[0] != '"')
            {
                return true;
            }

            // For path/directory targets the listing depends on the directory the
            // token points into; when it changed we must re-enumerate.
            if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
            {
                string directory = LocalCompletionItems.GetDeviceDirectoryPrefix(extracted);
                if (!string.Equals(directory, _itemsDirectory, StringComparison.Ordinal))
                {
                    LoadItems();
                }
            }

            UpdatePopup();
            return false;
        }

        // The prefix of the names that matches the word: what follows its last slash.
        private string GetItemFilter()
        {
            string tokenText = GetTokenText();
            if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
            {
                string path = ShellCommandLine.Unquote(tokenText);
                return path.Substring(path.LastIndexOf('/') + 1);
            }
            return tokenText.Trim('\'', '"');
        }

        // Each listing request gets a number: the answer to an older one (the word moved to another directory
        // meanwhile) is dropped.
        private int _listingGeneration;
        private bool _disposed;

        private void RequestDeviceListing(string directory)
        {
            int generation = ++_listingGeneration;
            bool directoriesOnly = _target == CompletionTarget.Directory;
            DeviceDirectoryListerHandler lister = _terminal.DeviceDirectoryLister;
            bool started = lister != null && _terminal.CanListDeviceDirectory && lister(directory, entries =>
            {
                if (_disposed || generation != _listingGeneration)
                {
                    return;
                }
                _items = LocalCompletionItems.BuildPathItems(directory, entries, directoriesOnly);
                OnItemsLoaded();
            });
            if (!started)
            {
                // No shell to ask (a bootloader, a password prompt, characters echoed to the device as they are typed):
                // nothing is offered rather than files of this computer.
                _items = new List<CompletionItem>();
                _terminal.BeginInvoke((Action)(() =>
                {
                    if (!_disposed && generation == _listingGeneration)
                    {
                        UpdatePopup();
                    }
                }));
            }
        }

        private void OnItemsLoaded()
        {
            UpdatePopup();
            if (_autoApply)
            {
                ApplyBestMatch();
            }
        }

        private void UpdatePopup()
        {
            if (_items == null || _disposed) return;

            string filter = GetItemFilter();

            var filtered = _items.Where(item => item.Text.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            _popup.SetItemList(filtered);
            _popup.SetFilter(filter);
        }

        private void ApplyBestMatch()
        {
            if (_items == null) return;
            string filter = GetItemFilter();

            var matches = _items.Where(item => item.Text.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                ApplyItem(matches[0], commit: false);
            }
            else if (matches.Length > 1)
            {
                string common = matches[0].Text;
                foreach (var item in matches.Skip(1))
                {
                    common = LocalCompletionItems.CommonPrefix(common, item.Text);
                }
                if (common.Length > 0 && common != filter)
                {
                    // A path keeps the directory part of the word in front of the common start of the names.
                    string insertText = (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
                        ? ShellCommandLine.EscapeWord(_itemsDirectory + common)
                        : common;
                    ApplyItem(new CompletionItem { Text = common, InsertText = insertText }, commit: false);
                }
            }
        }

        private void OnItemSelected(CompletionItem item, VirtualKey keys)
        {
            ApplyItem(item, keys == VirtualKey.Return);
        }

        private void ApplyItem(CompletionItem item, bool commit, string suffix = "")
        {
            if (item.InsertText == null) return;

            string insertText = item.InsertText;
            // Paths are the device's: its separator is "/" (the one of Windows used to be put in).
            bool isDir = insertText.EndsWith("/", StringComparison.Ordinal);

            if (isDir)
            {
                if (suffix == "/") suffix = "";
                else if (commit) insertText = insertText.TrimEnd('/');
            }

            string newText = _commandText.Substring(0, _tokenStart) + insertText + _commandText.Substring(_tokenEnd) + suffix;
            int caretOffset = 0;

            var args = new CompletionSession.AppliedEventArgs
            {
                NewText = newText,
                CaretIndex = _tokenStart + insertText.Length + (suffix ?? "").Length + caretOffset,
                Commit = commit,
                Item = item,
                IsDirectory = isDir
            };
            ItemApplied?.Invoke(this, args);
        }

        public void Dispose()
        {
            _disposed = true;
            _popup?.Close();
        }
    }

    /// <summary>
    /// Lists <c>directory</c> (the directory part of the word, as typed; empty for the shell's own directory) on the device and calls
    /// <c>done</c> on the UI thread with the names, or with null when the device did not answer. False when no listing could be started.
    /// </summary>
    public delegate bool DeviceDirectoryListerHandler(string directory, Action<IReadOnlyList<DeviceDirectoryEntry>> done);

    /// <summary>Set by the device terminal: asks the shell of the device for the names that the path completion offers.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DeviceDirectoryListerHandler DeviceDirectoryLister { get; set; }

    /// <summary>
    /// True when the shell of the device can be asked for a listing now: a Linux shell prompt is shown with the input
    /// line, and nothing typed has gone to the device yet (with "Echo typed characters" the device holds a part of the
    /// line, which the listing command would be appended to).
    /// </summary>
    internal bool CanListDeviceDirectory => _inputLine != null && _shellPromptSeen && !_localEcho
        && !LineInputText.LooksLikeSecretPrompt(_inputLine.Prompt);

    private LocalCompletionSession _completionSession;
    private ICompletionPopup _completionPopup;

    public event EventHandler<CompletionPopupRequest> CompletionPopupRequested;

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

    /// <summary>Raised when the user submits a command line with Enter, before it is sent to the device.</summary>
    public event EventHandler<CommandSubmittedEventArgs> CommandSubmitted;

    /// <summary>
    /// Commands offered by Tab completion for the first word of a line, in addition to the built-in ones: the user's
    /// command lists and the saved history of the device.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> CommandSuggestions { get; set; } = new string[0];

    private IEnumerable<CompletionItem> GetSuggestedCommandItems()
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

    public SerialTerminalControl()
    {
    }

    // Same meaning as in SshTerminalControl: at the prompt the user edits the local input line,
    // keys are forwarded to the device only while no input line is shown.
    protected override bool UsesTerminalInput => _inputLine == null;

    // Watches the escape sequences received while a command runs to tell a program that draws the whole screen
    // (mc, htop, vi, nano ...) from a shell that is back at its prompt.
    private readonly RemoteProgramTracker _remoteProgram = new RemoteProgramTracker();

    /// <summary>True while the local input line is shown, i.e. the keys are edited here instead of being sent to the device.</summary>
    public bool HasInputLine => _inputLine != null;

    /// <summary>True while a program that draws the whole screen seems to run on the device and gets every key.</summary>
    public bool FullScreenProgramActive => _inputLine == null && _remoteProgram.OwnsScreen;

    /// <summary>Raised when <see cref="HasInputLine"/>, <see cref="FullScreenProgramActive"/> or the prompt changes.</summary>
    public event EventHandler InputLineStateChanged;

    /// <summary>Announces a change of the input line or the full-screen state, once per change.</summary>
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

    /// <summary>
    /// Shows the local input line again, whatever the terminal believes the device is doing. For a program that
    /// ended without restoring the terminal (killed, crashed) or a prompt that is not recognized as one.
    /// </summary>
    public void ReturnToInputLine()
    {
        _remoteProgram.Reset();
        if (_inputLine != null)
        {
            return;
        }
        if (ActiveBufferIndex != 0)
        {
            ActiveBufferIndex = 0;
        }
        if (!ContinueDevicePrompt())
        {
            ShowPrompt();
        }
        NotifyInputLineState();
    }

    /// <summary>
    /// Put the command that sets the terminal size (COLUMNS, LINES, stty) in front of the next command when the
    /// terminal window has another size than the one the device was told last. Only after the device has shown a Linux shell prompt, and never for a password.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SyncRemoteSize { get; set; }

    // The size the device was told last (empty: not told on this connection yet).
    private Size _announcedSize = Size.Empty;

    // The device has shown the prompt of a Linux shell ("# " or "$ ") and not another kind of prompt since.
    private bool _shellPromptSeen;

    /// <summary>True when "Send terminal size now" can run: the input line is shown and nothing is typed in it.</summary>
    public bool CanSendRemoteSize => _inputLine != null && _inputLine.Text.Length == 0 && LineInputText.BuildRemoteSizeCommand(TerminalSize.Width, TerminalSize.Height) != null;

    /// <summary>True when <see cref="SubmitCommandNow"/> can run: the input line is shown (the device is at its prompt) and nothing is typed in it.</summary>
    public bool CanSubmitCommandNow => _inputLine != null && _inputLine.Text.Length == 0;

    /// <summary>
    /// Sends <paramref name="command"/> to the device as a command of its own, ended with <paramref name="lineEnding"/> instead of
    /// the Enter of the connection (it is not put in the history). What the command starts then reads the keys from the device
    /// line: the text sent after it with SendToDevice goes there. False, and nothing is sent, when <see cref="CanSubmitCommandNow"/> is false.
    /// </summary>
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
        Size size = TerminalSize;
        _announcedSize = size;
        SubmitCommand(LineInputText.BuildRemoteSizeCommand(size.Width, size.Height));
        return true;
    }

    // The terminal size command with "; " after it for the command about to be sent, or nothing.
    private string TakeRemoteSizePrefix(string command, string prompt)
    {
        if (!SyncRemoteSize || !_shellPromptSeen || command.Length == 0 || LineInputText.LooksLikeSecretPrompt(prompt))
        {
            return string.Empty;
        }
        Size size = TerminalSize;
        string sizeCommand = LineInputText.BuildRemoteSizeCommand(size.Width, size.Height);
        if (sizeCommand == null || size == _announcedSize)
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

    public string CurrentPrompt => _currentPrompt;

    public IReadOnlyList<string> CommandHistory => _history.Items;

    /// <summary>
    /// When true, the characters typed while no input line is shown (a login prompt, a program that reads single
    /// characters) are also drawn by the terminal itself, with Enter and Backspace; for connections whose far end does
    /// not echo (telnet without the server's echo, line mode). Typing in the input line needs no echo.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool EchoRawInput { get; set; }

    private bool _inputLineDisabled;

    /// <summary>
    /// When true, the local input line is never shown, whatever the device prints: every key goes to the device at once
    /// (a netconsole receiver in character mode, or one whose line is typed in a box of its own). An input line that is
    /// shown when this is switched on is taken off the screen with what was typed in it.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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

    /// <summary>Takes the input line off the screen (the prompt of the device stays) and gives the keys to the device.</summary>
    private void DropInputLine()
    {
        if (_inputLine == null)
        {
            return;
        }
        _history.EndSearch();
        ReplaceAfterDevicePrompt(string.Empty);
        OnTextReplaced();
        int row = _inputLine.StartLine - ActiveBuffer.FirstRow;
        if (row >= 0)
        {
            base.CursorPosition = new Point(_inputLine.StartColumn, row);
        }
        _inputLine = null;
        NavigationLocked = true;
        UpdateCaret();
        NotifyInputLineState();
    }

    /// <summary>
    /// Shows text that was not received from the device (what was sent, in the colour of echoed characters), on a line
    /// of its own.
    /// </summary>
    public void ShowLocalText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        if (CursorPosition.X != 0)
        {
            text = "\r\n" + text;
        }
        ProcessLocalOutput(text);
    }

    // How many typed characters of the current line Backspace may still erase on the screen.
    private int _rawEchoLength;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LocalEcho
    {
        get => _localEcho;
        set => _localEcho = value;
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
        if (!_history.Remove(commands))
        {
            return;
        }
        RefreshHistorySuggestion();
    }

    /// <summary>
    /// Puts a command from the history window into the input line in place of what is typed there, ready to be sent
    /// with Enter or edited, as Up does. Without an input line the text goes to the device as typed.
    /// </summary>
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
        RewrapInput();
        UpdateCaretFromInputLine();
        // As Up: with "Echo typed characters" the device gets the line.
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    public void AddToHistory(string command)
    {
        _history.Add(command);
    }

    /// <summary>
    /// The device has just printed its prompt: the input line continues that very line after the device's cursor,
    /// instead of a second, local prompt on a line of its own ("root@OpenWrt:/# " followed by "# "). False when the
    /// cursor is not at the end of a prompt (something follows it, or the text before it does not end like one).
    /// </summary>
    private bool ContinueDevicePrompt()
    {
        if (_inputLineDisabled || ActiveBufferIndex != 0 || _visibleColumns <= 0)
        {
            return false;
        }
        EditableTextDocument document = ((EditableTextBrowser)this).EditableDocument;
        TextDocument.TextPosition cursor = ToDocumentPosition(CursorPosition);
        if (cursor.Line < 0 || cursor.Line >= document.LineCount || cursor.Column <= 0 || cursor.Column >= _visibleColumns)
        {
            return false;
        }
        // A prompt is the last thing on the screen.
        string lineText = document.GetLineText(cursor.Line);
        if (lineText.Length > cursor.Column && !string.IsNullOrWhiteSpace(lineText.Substring(cursor.Column)))
        {
            return false;
        }
        for (int line = cursor.Line + 1; line < document.LineCount; line++)
        {
            if (!string.IsNullOrWhiteSpace(document.GetLineText(line)))
            {
                return false;
            }
        }
        // The blank after "# " is part of the prompt even where the line does not keep trailing blanks.
        string prompt = lineText.Length >= cursor.Column ? lineText.Substring(0, cursor.Column) : lineText.PadRight(cursor.Column);
        if (!LineInputText.EndsLikePrompt(prompt, _currentPrompt))
        {
            return false;
        }
        if (cursor.Line + 1 < document.LineCount)
        {
            document.RemoveLines(cursor.Line + 1, document.LineCount - cursor.Line - 1);
        }
        _suggestion.Clear();
        _inputLine = new InputLine(cursor.Line, prompt.Length, prompt);
        RewrapInput();
        OnTextReplaced();
        UpdateCaretFromInputLine();
        NavigationLocked = false;
        _history.ResetNavigation();
        _history.EndSearch();
        return true;
    }

    /// <summary>
    /// Replaces what follows the device's prompt on the screen (the typed text, wrapped over as many lines as it
    /// needs) with <paramref name="text"/>. The prompt itself is not rewritten, so it keeps the device's colours.
    /// </summary>
    private void ReplaceAfterDevicePrompt(string text)
    {
        EditableTextDocument document = ((EditableTextBrowser)this).EditableDocument;
        int line = _inputLine.StartLine;
        if (line >= document.LineCount)
        {
            return;
        }
        if (document.LineCount > line + 1)
        {
            document.RemoveLines(line + 1, document.LineCount - line - 1);
        }
        int start = _inputLine.StartColumn;
        string lineText = document.GetLineText(line);
        if (lineText.Length < start)
        {
            text = new string(' ', start - lineText.Length) + text;
            start = lineText.Length;
        }
        if (lineText.Length == start && text.Length == 0)
        {
            return;
        }
        ReplaceTextCore(new TextDocument.TextPosition(start, line), new TextDocument.TextPosition(lineText.Length, line), select: false, text);
    }

    private void ShowPrompt()
    {
        if (_inputLineDisabled)
        {
            return;
        }
        // Remove empty last line if exists
        if (((EditableTextBrowser)this).EditableDocument.LineCount > 0)
        {
            string lastLine = ((EditableTextBrowser)this).EditableDocument.GetLineText(((EditableTextBrowser)this).EditableDocument.LineCount - 1);
            if (string.IsNullOrWhiteSpace(lastLine))
            {
                ((EditableTextBrowser)this).EditableDocument.RemoveLines(((EditableTextBrowser)this).EditableDocument.LineCount - 1, 1);
            }
        }

        int startLine = ((EditableTextBrowser)this).EditableDocument.LineCount;
        int startColumn = 0;
        _suggestion.Clear();
        _inputLine = new InputLine(startLine, startColumn, _currentPrompt);
        ((EditableTextBrowser)this).EditableDocument.AppendLine(_currentPrompt);
        RewrapInput();
        OnTextReplaced();
        SetCaret(((EditableTextBrowser)this).EditableDocument.EndPosition);
        NavigationLocked = false;
        _history.ResetNavigation();
        _history.EndSearch();
    }

    private bool RewrapInput(int? caretIndex = null)
    {
        if (_inputLine == null) return false;

        string fullText = _inputLine.Prompt + _inputLine.Text;
        if (string.IsNullOrEmpty(fullText)) return false;

        int maxWidth = _visibleColumns;
        if (maxWidth <= 0) return false;

        StringBuilder wrapped = new StringBuilder();
        for (int i = 0; i < fullText.Length; i += maxWidth)
        {
            int len = Math.Min(maxWidth, fullText.Length - i);
            wrapped.Append(fullText.Substring(i, len));
            if (i + maxWidth < fullText.Length)
                wrapped.Append("\n");
        }

        if (_inputLine.StartColumn > 0 && _inputLine.StartColumn < maxWidth)
        {
            // The prompt is the device's: it stays as the device drew it, only the text after it is redrawn.
            ReplaceAfterDevicePrompt(wrapped.ToString().Substring(_inputLine.StartColumn));
        }
        else
        {
            ReplaceTextCore(
                new TextDocument.TextPosition(0, _inputLine.StartLine),
                new TextDocument.TextPosition(0, ((EditableTextBrowser)this).EditableDocument.LineCount),
                select: false,
                wrapped.ToString());
        }

        OnTextReplaced();

        int resolvedCaret = caretIndex ?? _inputLine.CaretIndex;
        _inputLine.SetCaretIndex(resolvedCaret);
        return true;
    }

    private TextDocument.TextPosition GetCaretPositionFromInputIndex(int inputIndex)
    {
        if (_inputLine == null) return new TextDocument.TextPosition(0, 0);

        int fullIndex = _inputLine.Prompt.Length + inputIndex;
        int line = _inputLine.StartLine + fullIndex / _visibleColumns;
        int column = fullIndex % _visibleColumns;
        return new TextDocument.TextPosition(column, line);
    }

    private bool _caretClampedFromOutput;

    private void UpdateCaretFromInputLine()
    {
        if (_inputLine == null) return;
        SetCaret(GetCaretPositionFromInputIndex(_inputLine.CaretIndex));
    }

    /// <summary>
    /// Takes the caret of the screen into the input line. The arrow keys, Home/End and a mouse click move only the caret
    /// of the screen, so a paste, a quick command or the completion used the place of the last typed character instead
    /// of the one the caret shows (the smart terminal reads its caret from the screen as well).
    /// </summary>
    private void SyncInputCaretFromScreen()
    {
        if (_inputLine == null || _visibleColumns <= 0 || _caretClampedFromOutput) return;

        TextDocument.TextPosition caret = CaretPosition;
        string fullText = _inputLine.Prompt + _inputLine.Text;
        int line = caret.Line - _inputLine.StartLine;
        int lines = Math.Max(1, (fullText.Length + _visibleColumns - 1) / _visibleColumns);
        if (line < 0 || line > lines)
        {
            // The caret is not in the input block (the text above was being selected): the input line keeps its own.
            return;
        }
        int fullIndex = line * _visibleColumns + Math.Max(0, caret.Column);
        int inputIndex = fullIndex - _inputLine.Prompt.Length;
        _inputLine.SetCaretIndex(Math.Min(Math.Max(inputIndex, 0), _inputLine.Text.Length));
    }

    // ----------------------------------------------------------------------
    // §3.3 ReplaceText routing. Every edit that lands inside the input area is
    // routed through ReplaceInputLineText so an open completion (session +
    // popup) is kept consistent with the edited text. This mirrors the
    // ReplaceText funnel SshTerminalControl uses for its completion session.
    // ----------------------------------------------------------------------

    private void ReplaceInputLineText(int startIndex, int oldLength, string replacement)
    {
        if (_inputLine == null) return;
        if (startIndex < 0 || startIndex > _inputLine.Text.Length) return;

        string text = replacement ?? "";
        int newLength = text.Length;

        string newText = _inputLine.Text.Substring(0, startIndex)
            + text
            + _inputLine.Text.Substring(startIndex + oldLength);

        _inputLine.SetText(newText);
        _inputLine.SetCaretIndex(startIndex + newLength);
        RewrapInput();
        UpdateCaretFromInputLine();

        SyncCompletionAfterTextChange(startIndex, oldLength, newLength);
    }

    // Keeps an open completion consistent with the last input-line text edit.
    // Closes session + popup when the edit invalidated the current token.
    private void SyncCompletionAfterTextChange(int offset, int oldLength, int newLength)
    {
        LocalCompletionSession session = _completionSession;
        if (session == null || _inputLine == null) return;

        if (session.OnTextChanged(_inputLine.Text, offset, oldLength, newLength))
        {
            CloseCompletion();
        }
    }

    // Overrides the base editable-text path so replacements that land inside
    // the (prompt + input) block go through the input line instead of bypassing
    // it; edits outside that block keep the default behavior.
    public override void ReplaceText(TextDocument.TextPosition start, TextDocument.TextPosition end, bool select, string text)
    {
        if (_inputLine != null)
        {
            int startIndex = GetInputIndexFromDocumentPosition(start);
            int endIndex = GetInputIndexFromDocumentPosition(end);
            if (startIndex >= 0 && endIndex >= startIndex)
            {
                ReplaceInputLineText(startIndex, endIndex - startIndex, text);
                return;
            }
        }
        base.ReplaceText(start, end, select, text);
    }

    // Maps a document position to an index into _inputLine.Text (without the
    // prompt), or -1 when the position lies outside the input block on screen.
    private int GetInputIndexFromDocumentPosition(TextDocument.TextPosition position)
    {
        if (_inputLine == null || _visibleColumns <= 0) return -1;

        string fullText = _inputLine.Prompt + _inputLine.Text;
        int lines = (fullText.Length + _visibleColumns - 1) / _visibleColumns;
        int line = position.Line - _inputLine.StartLine;
        if (line < 0 || line >= lines) return -1;
        if (position.Column < 0 || position.Column >= _visibleColumns) return -1;

        int fullIndex = line * _visibleColumns + position.Column;
        if (fullIndex > fullText.Length) return -1;

        int inputIndex = fullIndex - _inputLine.Prompt.Length;
        if (inputIndex < 0 || inputIndex > _inputLine.Text.Length) return -1;
        return inputIndex;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Handle completion popup key events
        if (_completionPopup != null && _completionPopup.TryHandleKeyEvent(e.ToKeyStroke()))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // The grey suggestion from the history (Right, End, Ctrl+Right at the end of the line).
        if (TryAcceptHistorySuggestion(e))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle completion (Tab)
        if (e.KeyCode == Keys.Tab && !e.Control && !e.Shift && !e.Alt && _inputLine != null)
        {
            ShowCompletions();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle completion (Ctrl+Space)
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.Space && _inputLine != null)
        {
            ShowCompletions();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle history search (Ctrl+R). Without an input line the key belongs to the program on the device
        // (Ctrl+R is "reread directory" in mc), so it is not taken here.
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.R && _inputLine != null)
        {
            HandleHistorySearch();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle history navigation (Up/Down arrows)
        if (!e.Control && !e.Shift && !e.Alt && _inputLine != null)
        {
            if (e.KeyCode == Keys.Up)
            {
                NavigateHistory(forward: false);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            else if (e.KeyCode == Keys.Down)
            {
                NavigateHistory(forward: true);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        // Handle Escape to cancel history search or completion
        if (e.KeyCode == Keys.Escape)
        {
            if (_history.IsSearching)
            {
                CancelHistorySearch();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (_completionSession != null)
            {
                CloseCompletion();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            // Then the suggestion, until the line is changed.
            if (_suggestion.Current != null && _inputLine != null)
            {
                _suggestion.Dismiss(_inputLine.Text);
                RefreshHistorySuggestion();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        // Handle Enter to submit command
        if (e.KeyCode == Keys.Enter && !e.Control && !e.Shift && !e.Alt && _inputLine != null)
        {
            SubmitCommand();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle Ctrl+C to clear current line
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.C && _inputLine != null)
        {
            ClearCurrentLine();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // Handle Ctrl+L to clear screen and redraw prompt
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.L && _inputLine != null)
        {
            ClearScreenAndRedrawPrompt();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_inputLine == null && EchoRawInput && !e.Control && !e.Alt)
        {
            // Backspace is echoed in OnKeyPress, after it was sent: in byte mode the number of DELs depends on the
            // letter that is still shown before the cursor.
            if (e.KeyCode == Keys.Enter)
            {
                ProcessLocalOutput("\r\n");
                _rawEchoLength = 0;
            }
        }

        base.OnKeyDown(e);
    }

    private void ShowCompletions()
    {
        if (_inputLine == null) return;

        // Close existing completion
        if (_completionSession != null)
        {
            CloseCompletion();
        }

        SyncInputCaretFromScreen();
        string text = _inputLine.Text;
        int caretIndex = _inputLine.CaretIndex;

        // Tokenize the input to find the current token
        var tokens = Tokenize(text);
        int? matchIndex = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Contains(caretIndex))
            {
                matchIndex = i;
                break;
            }
        }

        CompletionTarget target = CompletionTarget.Command;
        int tokenStart = 0;
        int tokenEnd = text.Length;

        if (matchIndex.HasValue)
        {
            var token = tokens[matchIndex.Value];
            tokenStart = token.Start;
            tokenEnd = token.End;

            // Determine completion target based on position
            if (matchIndex == 0)
            {
                target = CompletionTarget.Command;
            }
            else
            {
                // Check if previous token is "cd" for directory completion
                var prevToken = tokens[matchIndex.Value - 1];
                string prevText = prevToken.Extract(text);
                if (prevText == "cd" || prevText == "ls" || prevText == "dir")
                {
                    target = CompletionTarget.Directory;
                }
                else
                {
                    target = CompletionTarget.Path;
                }
            }
        }
        else
        {
            // The caret is between words or after the last one ("rm |"): an empty word at the caret is completed. The
            // word used to start at the end of the previous one, so the chosen path replaced the blank after it.
            tokenStart = caretIndex;
            tokenEnd = caretIndex;
            CompletionSession.Token? previous = null;
            foreach (var token in tokens)
            {
                if (token.End < caretIndex)
                {
                    previous = token;
                }
            }
            if (previous.HasValue)
            {
                string prevText = previous.Value.Extract(text);
                target = (prevText == "cd" || prevText == "ls" || prevText == "dir") ? CompletionTarget.Directory : CompletionTarget.Path;
            }
            else
            {
                target = CompletionTarget.Command;
            }
        }

        // Request completion popup
        var request = new CompletionPopupRequest
        {
            ScreenLocation = PointToScreen(Point.Round(PositionToPoint(CaretPosition.Column, CaretPosition.Line))).ToGridPoint(),
            LineHeight = LineHeight,
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

    private void OnCompletionApplied(object sender, CompletionSession.AppliedEventArgs args)
    {
        if (_inputLine != null)
        {
            _inputLine.SetText(args.NewText);
            RewrapInput();
            _inputLine.SetCaretIndex(args.CaretIndex - _inputLine.Prompt.Length);
            UpdateCaretFromInputLine();
        }

        if (args.Commit)
        {
            SubmitCommand();
        }
        else
        {
            // Continue completion if it's a directory
            if (args.IsDirectory)
            {
                ShowCompletions();
            }
            else
            {
                // §3.3: a completed non-directory token cannot keep listing -
                // dismiss the popup so it never shows a stale list while the
                // user keeps typing.
                CloseCompletion();
            }
        }
    }

    private void CloseCompletion()
    {
        _completionSession?.Dispose();
        _completionSession = null;
        _completionPopup = null;
    }

    // Tokenize input text for completion
    private static CompletionSession.Token[] Tokenize(string text)
    {
        return CompletionSession.Tokenize(text);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_inputLine == null)
        {
            if (EchoRawInput && !char.IsControl(e.KeyChar))
            {
                _rawEchoLength++;
                ProcessLocalOutput(e.KeyChar.ToString());
            }
            base.OnKeyPress(e);
            if (EchoRawInput && e.KeyChar == '\b' && _rawEchoLength > 0)
            {
                ProcessLocalOutput("\b \b");
                _rawEchoLength--;
            }
            return;
        }

        // Typing goes where the caret is shown, after the arrows or a click moved it.
        SyncInputCaretFromScreen();

        // Handle printable characters
        if (!char.IsControl(e.KeyChar))
        {
            ReplaceInputLineText(_inputLine.CaretIndex, 0, e.KeyChar.ToString());

            // Local echo for typed characters
            if (_localEcho)
            {
                SendText(e.KeyChar.ToString(), InputKind.Typed);
            }

            // Trigger completion on space or slash (for paths)
            if ((e.KeyChar == ' ' || e.KeyChar == '/') && _completionPopup == null)
            {
                ShowCompletions();
            }

            e.Handled = true;
            return;
        }

        // Handle Backspace
        if (e.KeyChar == '\b')
        {
            if (_inputLine.CaretIndex > 0)
            {
                ReplaceInputLineText(_inputLine.CaretIndex - 1, 1, "");
            }
            e.Handled = true;
            return;
        }

        // Handle Enter (already handled in OnKeyDown, but just in case)
        if (e.KeyChar == '\r' || e.KeyChar == '\n')
        {
            SubmitCommand();
            e.Handled = true;
            return;
        }

        base.OnKeyPress(e);
    }

    // Ctrl+R: the input line shows the newest command that contains what was typed, the next press the one before.
    // Nothing is printed through the emulator: that would be written at the device's cursor, into the input line.
    private void HandleHistorySearch()
    {
        if (_inputLine == null) return;

        string? match = _history.NextSearchMatch(_inputLine.Text);
        if (match != null)
        {
            _inputLine.SetText(match);
            RewrapInput();
            UpdateCaretFromInputLine();
        }
    }

    private void CancelHistorySearch()
    {
        if (_history.IsSearching && _inputLine != null)
        {
            _inputLine.SetText(_history.SearchOriginalInput);
            RewrapInput();
            UpdateCaretFromInputLine();
            _history.EndSearch();
        }
    }

    // Up (forward == false) and Down. On a line with text only the commands that start with it are shown (unless that is
    // turned off in the settings), as history-search-backward does in bash; Down past the newest one brings the typed
    // line back.
    private void NavigateHistory(bool forward)
    {
        if (_inputLine == null || _history.Count == 0) return;

        string? shown = _history.Navigate(_inputLine.Text, older: !forward, HistoryPrefixSearchEnabled);
        if (shown == null) return;
        _inputLine.SetText(shown);
        RewrapInput();
        UpdateCaretFromInputLine();
        // Send the updated line to device for echo
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    private AutoCompletionTriggers? CompletionTriggers => AppSettings?.VirtualTerminal?.CompletionTriggers;

    // The options are the ones of Settings -> Command Completion, shared with the Smart Terminal; on by default.
    private bool HistorySuggestionsEnabled => CompletionTriggers?.HistorySuggestions ?? true;

    private bool HistoryPrefixSearchEnabled => CompletionTriggers?.HistoryPrefixSearch ?? true;

    private string? ComputeHistorySuggestion()
    {
        InputLine? inputLine = _inputLine;
        if (inputLine == null)
        {
            return null;
        }
        string text = inputLine.Text;
        return _suggestion.Compute(true, _inputLineDisabled, HasSelection, _visibleColumns, HistorySuggestionsEnabled,
            LineInputText.LooksLikeSecretPrompt(inputLine.Prompt), text,
            () => CaretPosition == GetCaretPositionFromInputIndex(text.Length), _history.NewestFirst);
    }

    /// <summary>Works out the grey suggestion for the line as it is now and repaints when it has changed.</summary>
    private void RefreshHistorySuggestion()
    {
        string? suggestion;
        try
        {
            suggestion = ComputeHistorySuggestion();
        }
        catch
        {
            // The caret is also moved while the input line is being redrawn; the next change works it out again.
            suggestion = null;
        }
        if (_suggestion.Show(suggestion))
        {
            Invalidate();
        }
    }

    /// <summary>
    /// Right or End at the end of the line take the whole suggestion, Ctrl+Right its next word, as in the Smart Terminal.
    /// False, leaving the key to its usual meaning, when no suggestion is shown.
    /// </summary>
    private bool TryAcceptHistorySuggestion(KeyEventArgs e)
    {
        InputLine? inputLine = _inputLine;
        if (inputLine == null || e.Shift || e.Alt)
        {
            return false;
        }
        bool wholeCommand = !e.Control && (e.KeyCode == Keys.Right || e.KeyCode == Keys.End);
        bool nextWord = e.Control && e.KeyCode == Keys.Right;
        if (!wholeCommand && !nextWord)
        {
            return false;
        }
        RefreshHistorySuggestion();
        string? suggestion = _suggestion.Current;
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
        ReplaceInputLineText(inputLine.Text.Length, 0, part);
        // As if the characters were typed.
        if (_localEcho)
        {
            SendText(part, InputKind.Typed);
        }
        return true;
    }

    protected override void PaintLineOverlay(Graphics graphics, int line, string text, int firstColumn, int left, int top, int height)
    {
        base.PaintLineOverlay(graphics, line, text, firstColumn, left, top, height);
        string? suggestion = _suggestion.Current;
        InputLine? inputLine = _inputLine;
        if (suggestion == null || inputLine == null || HasSelection || _visibleColumns <= 0)
        {
            return;
        }
        // The prompt and the text are wrapped at the width of the window (RewrapInput): the suggestion starts where they end.
        int fullIndex = inputLine.Prompt.Length + inputLine.Text.Length;
        if (line != inputLine.StartLine + fullIndex / _visibleColumns)
        {
            return;
        }
        PaintHistorySuggestion(graphics, suggestion, fullIndex % _visibleColumns - firstColumn, left, top);
    }

    private void SendLineToDevice()
    {
        if (_inputLine == null) return;
        
        // Only what was typed: the prompt is the device's own text (or a local marker), not input.
        SendText(_inputLine.Text, InputKind.Typed);
    }

    private void SubmitCommand()
    {
        SubmitCommand(null);
    }

    // commandOverride: a command made by the terminal itself (the terminal size), sent instead of the text of the
    // input line; it is not recorded in the history and not reported as typed by the user.
    // lineEndingOverride: what ends the command instead of the Enter of the connection (EnterSequence).
    private void SubmitCommand(string commandOverride, string lineEndingOverride = null)
    {
        if (_inputLine == null) return;

        bool typedByUser = commandOverride == null;
        string command = (commandOverride ?? _inputLine.Text).Trim();
        _history.EndSearch();

        if (typedByUser)
        {
            // What is typed at a "Password:" prompt is not kept in the history.
            if (!LineInputText.LooksLikeSecretPrompt(_inputLine.Prompt))
            {
                AddToHistory(command);
            }
            CommandSubmitted?.Invoke(this, new CommandSubmittedEventArgs(command, _inputLine.Text, _inputLine.Prompt));
        }

        if (ContinuesDevicePrompt)
        {
            // The device echoes the command right after its prompt, where its cursor still is, as in any terminal:
            // the locally typed copy is taken away so the command is not shown twice (and an empty Enter does not
            // leave a stray line).
            ReplaceAfterDevicePrompt(string.Empty);
            OnTextReplaced();
            int row = _inputLine.StartLine - ActiveBuffer.FirstRow;
            if (row >= 0)
            {
                base.CursorPosition = new Point(_inputLine.StartColumn, row);
            }
            NavigationLocked = true;
            UpdateCaret();
            UpdateLayout();
        }
        else
        {
            // Move to next line and show command output
            ((EditableTextBrowser)this).EditableDocument.AppendLine("");
            NavigationLocked = true;
            ActiveBuffer.FirstRow = ((EditableTextBrowser)this).EditableDocument.LineCount - 1;
            base.CursorPosition = new Point(0, 0);
            UpdateCaret();
            UpdateLayout();
        }

        // Send command to device
        string lineEnding = lineEndingOverride ?? EnterSequence ?? "\r";
        string sizePrefix = typedByUser ? TakeRemoteSizePrefix(command, _inputLine.Prompt) : string.Empty;
        SendText(sizePrefix + command + lineEnding, InputKind.Typed);

        // Reset input line
        _inputLine = null;
        // Whatever ran before is over; the command just sent starts with a clean slate.
        _remoteProgram.Reset();
        NotifyInputLineState();
    }

    // True when the input line continues the device's own prompt and the device echoes what it receives (telnet
    // without the server's echo shows typed text itself): the device then draws the command line, like any terminal.
    private bool ContinuesDevicePrompt => _inputLine != null && _inputLine.StartColumn > 0 && !EchoRawInput;

    private void ClearCurrentLine()
    {
        if (_inputLine == null) return;

        _history.EndSearch();
        _history.ResetNavigation();
        if (ContinuesDevicePrompt)
        {
            // The shell answers Ctrl+C itself with "^C" and a new prompt, which the input line then continues.
            ReplaceAfterDevicePrompt(string.Empty);
            OnTextReplaced();
            _inputLine = null;
            NavigationLocked = true;
            SendBytes(new byte[] { 3 }, InputKind.KeySequence);
            return;
        }
        _inputLine.SetText("");
        RewrapInput();
        UpdateCaretFromInputLine();

        // Show ^C indicator
        ProcessOutput("^C");
        // Send Ctrl+C to device
        SendBytes(new byte[] { 3 }, InputKind.KeySequence);
        ShowPrompt();
    }

    private void ClearScreenAndRedrawPrompt()
    {
        if (_inputLine == null) return;

        _history.EndSearch();
        // Only the screen here is cleared: the line is edited locally, the device has nothing to redraw (an escape
        // sequence sent to it would land in its own line editor as keys).
        ClearScreen();
        // Re-send the current line
        if (_localEcho)
        {
            SendLineToDevice();
        }
    }

    protected override TextDocument.TextPosition ComputeMovedPosition(CaretMove caretMove, Keys keys, TextDocument.TextPosition textPosition, bool extendSelection = false)
    {
        TextDocument.TextPosition movedPosition = base.ComputeMovedPosition(caretMove, keys, textPosition, extendSelection);

        if (_inputLine != null)
        {
            // Prevent moving caret before the prompt
            if (movedPosition < _inputLine.StartPosition)
            {
                movedPosition = _inputLine.StartPosition;
            }

            // Handle Home/End keys within input line
            if (caretMove == CaretMove.Home && (keys & Keys.Control) == 0)
            {
                movedPosition = _inputLine.StartPosition;
            }
            else if (caretMove == CaretMove.End && (keys & Keys.Control) == 0)
            {
                movedPosition = new TextDocument.TextPosition(
                    _inputLine.Prompt.Length + _inputLine.Text.Length,
                    _inputLine.StartLine);
            }
        }

        return movedPosition;
    }

    protected override void SetCaretCore(TextDocument.TextPosition textPosition, SelectionChangeSource selectionChangeSource, CaretDirection caretDirection = CaretDirection.Unspecified)
    {
        // A caret held at the start of the input line only because the mouse selected text above it (to copy it)
        // does not say where to type or paste.
        _caretClampedFromOutput = false;
        if (_inputLine != null)
        {
            // Constrain caret to input area
            if (textPosition < _inputLine.StartPosition)
            {
                textPosition = _inputLine.StartPosition;
                _caretClampedFromOutput = true;
            }
        }
        base.SetCaretCore(textPosition, selectionChangeSource, caretDirection);
        // The suggestion is shown only while the caret is at the end of the line.
        RefreshHistorySuggestion();
    }

    protected override void PlaceCaretAt(Point point)
    {
        if (_inputLine != null)
        {
            TextDocument.TextPosition textPosition = HitTest(point, allowPastEnd: true);
            if (textPosition.IsValid && !(textPosition < _inputLine.StartPosition))
            {
                SetCaret(textPosition);
                return;
            }
        }
        base.PlaceCaretAt(point);
    }

    protected override void SelectAll()
    {
        if (_inputLine != null)
        {
            SetSelectionEnd(new TextDocument.TextPosition(
                _inputLine.Prompt.Length + _inputLine.Text.Length,
                _inputLine.StartLine));
            SetSelection(select: true, _inputLine.StartPosition);
            return;
        }
        base.SelectAll();
    }

    protected override void AdjustSelectionEnd(ref TextDocument.TextPosition textPosition)
    {
        if (_inputLine != null && ((ReadonlyTextBrowser)this).HasSelection &&
            !(SelectionEnd < _inputLine.StartPosition) && textPosition < _inputLine.StartPosition)
        {
            textPosition = _inputLine.StartPosition;
        }
        base.AdjustSelectionEnd(ref textPosition);
    }

    // No input line until the device shows its prompt: the input line then continues it. Until then keys go to the
    // device as they are (a local "> " drawn at once would only stand in front of the device's own prompt).
    public override void BeginNewSession()
    {
        base.BeginNewSession();
        _inputLine = null;
        _remoteProgram.Reset();
        _announcedSize = Size.Empty;
        _shellPromptSeen = false;
        NotifyInputLineState();
    }

    // Also run for ESC c from the device, which prints its prompt again by itself.
    public override void Reset()
    {
        base.Reset();
        _inputLine = null;
        _remoteProgram.Reset();
        _history.EndSearch();
        _history.ResetNavigation();
        NotifyInputLineState();
    }

    /// <summary>
    /// Clears the screen for the person (the Clear button, Ctrl+L): when a command was being typed, its prompt and
    /// text are drawn again at the top, so typing goes on.
    /// </summary>
    public void ClearScreen()
    {
        InputLine previous = _inputLine;
        Reset();
        if (previous == null)
        {
            return;
        }
        if (previous.StartColumn > 0)
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
            RewrapInput();
            UpdateCaretFromInputLine();
        }
    }

    /// <summary>
    /// Tools > Quick commands: the command goes into the input line at the caret and is not sent; the user checks it and
    /// presses Enter. While a program of the device has the keys (no input line is shown) it is typed to the device instead.
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
        // The command continues from the text that a history search shows now.
        _history.EndSearch();
        SyncInputCaretFromScreen();
        ReplaceInputLineText(_inputLine.CaretIndex, 0, text);
        UpdateCaretFromInputLine();
        // Like a pasted text: with "Echo typed characters" the device gets the characters as they are put in.
        if (_localEcho)
        {
            SendText(text, InputKind.Paste);
        }
    }

    public override void PasteText(string clipboardText)
    {
        if (UsesTerminalInput)
        {
            base.PasteText(clipboardText);
            return;
        }
        PasteIntoInputLine(clipboardText, PasteModeInUse);
    }

    /// <summary>
    /// Pastes into the input line the way the smart terminal does: the text goes where the caret is (a selection inside
    /// the line is replaced), several lines become one command line as a shell would run them ("; " between commands,
    /// a wrapped command joined with a space), and nothing is sent before Enter. False when no input line is shown.
    /// </summary>
    public bool PasteIntoInputLine(string clipboardText, PasteMode mode)
    {
        if (_inputLine == null)
        {
            return false;
        }
        try
        {
            string text = PasteGuard.Prepare(PasteGuard.ApplyMode(clipboardText, mode), AppSettings, linesRunAtOnce: false, owner: this);
            text = ShellCommandLine.JoinPastedLines(text);
            // The device may use an encoding without some of the characters of the text.
            text = !string.IsNullOrEmpty(text) ? PasteGuard.FitToEncoding(text, InputEncoding, AppSettings, this) : null;
            if (_inputLine == null || string.IsNullOrEmpty(text))
            {
                return true;
            }
            // The command continues from the text that a history search shows now.
            _history.EndSearch();

            int start;
            int length = 0;
            int selectionStart = HasSelection ? GetInputIndexFromDocumentPosition(SelectionStart) : -1;
            int selectionEnd = HasSelection ? GetInputIndexFromDocumentPosition(CaretPosition) : -1;
            if (selectionStart >= 0 && selectionEnd >= 0 && !_caretClampedFromOutput)
            {
                // A selection inside the input line is replaced, as in any editor.
                start = Math.Min(selectionStart, selectionEnd);
                length = Math.Abs(selectionEnd - selectionStart);
            }
            else
            {
                SyncInputCaretFromScreen();
                start = _inputLine.CaretIndex;
            }
            if (HasSelection)
            {
                SetSelection(select: false, CaretPosition);
                Invalidate();
            }
            ReplaceInputLineText(start, length, text);
            UpdateCaretFromInputLine();
            // With "Echo typed characters" the device gets the characters as they are put in.
            if (_localEcho)
            {
                SendText(text, InputKind.Paste);
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
        return true;
    }

    public void OnDataReceived(string text)
    {
        try
        {
            OnDataReceivedCore(text);
        }
        finally
        {
            // Showing or hiding the input line, and a full-screen program taking over or leaving, are reported here
            // for every path above, including the early returns.
            NotifyInputLineState();
        }
    }

    private void OnDataReceivedCore(string text)
    {
        if (ContinuesDevicePrompt && !string.IsNullOrEmpty(text) && LineInputText.TrimTrailingControlSequences(text).Length > 0)
        {
            // The device prints something while a command is being typed after its prompt (a kernel message, a
            // late line of output): it would be written over the typed text.
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
            // The device keeps talking while the input line is shown: a shell prompt that arrives now still counts.
            TrackShellPrompt(text);
            return;
        }

        // A command is running. If it is a program that draws the whole screen (mc, htop, top, vi, nano ...), what
        // it prints is its own output, however much of it looks like a shell prompt ("$ " in the command line of
        // mc, "> " in a menu). Showing the input line then would take the keyboard from it (arrows would walk the
        // history, Tab would open the completion, Enter would send a line) and would draw into its screen.
        // ProcessOutput above has applied the escape sequences already, so the emulator knows the alternate screen.
        _remoteProgram.Observe(text, ActiveBufferIndex != 0);
        if (_remoteProgram.OwnsScreen)
        {
            return;
        }

        TrackShellPrompt(text);

        // Auto-detect prompt from received data
        if (_autoDetectPrompt)
        {
            DetectPrompt(text);
        }

        // If we're at a prompt, show new prompt. Only line breaks are trimmed: the blank after "> ", "# ", "$ "
        // is part of the pattern (TrimEnd() used to remove it, so the prompt never came back).
        if (LineInputText.EndsLikePrompt(text, _currentPrompt))
        {
            if (!ContinueDevicePrompt())
            {
                ShowPrompt();
            }
        }
    }

    /// <summary>
    /// Shows text that the device printed while a command was being typed after its prompt: the typed text is taken
    /// off the screen, the device's text is printed, and then the input line comes back - after the device's new
    /// prompt when the text ends with one, otherwise after a copy of the old prompt on the next line - with what was
    /// typed so far and the caret where it was.
    /// </summary>
    private void PrintAboveInputLine(string text)
    {
        InputLine previous = _inputLine;
        ReplaceAfterDevicePrompt(string.Empty);
        OnTextReplaced();
        _inputLine = null;
        ProcessOutput(text);
        TrackShellPrompt(text);
        bool continued = LineInputText.EndsLikePrompt(text, _currentPrompt) && ContinueDevicePrompt();
        if (!continued)
        {
            if (CursorPosition.X != 0)
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
            _inputLine.SetText(previous.Text);
            _inputLine.SetCaretIndex(previous.CaretIndex);
            int caret = previous.CaretIndex;
            RewrapInput(caret);
            UpdateCaretFromInputLine();
        }
    }

    private void DetectPrompt(string text)
    {
        // What the shell sends after its prompt without printing anything (BusyBox ash asks for the cursor position
        // with ESC[6n) does not belong to the prompt.
        text = LineInputText.TrimTrailingControlSequences(text);
        // Common prompt patterns
        string[] promptPatterns = { "> ", "# ", "$ ", ">> ", "] ", "-> " };

        foreach (string pattern in promptPatterns)
        {
            int lastIndex = text.LastIndexOf(pattern);
            if (lastIndex >= 0 && lastIndex + pattern.Length == text.Length)
            {
                _currentPrompt = pattern;
                return;
            }
        }
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

    internal override ILinkScanner CreateLinkScanner()
    {
        return new LocalLinkScanner(this);
    }

    // The terminal as the host of the scanner of the links in its text (Terminal/LocalLinkScanner.cs).
    bool ILocalLinkHost.PathExists(string path)
    {
        return PathExistence.Exists(path);
    }

    void ILocalLinkHost.RequestOpenFile(string path)
    {
        RequestOpenFile(path);
    }
}