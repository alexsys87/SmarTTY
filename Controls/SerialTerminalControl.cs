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

public class SerialTerminalControl : TerminalEmulatorControl
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

    private class HistorySearchSession
    {
        public readonly List<string> Matches;
        public int CurrentIndex;
        public readonly string OriginalInput;

        public HistorySearchSession(List<string> history, string searchTerm, string originalInput)
        {
            OriginalInput = originalInput;
            Matches = history
                .Where(cmd => cmd.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                .Reverse()
                .ToList();
            CurrentIndex = -1;
        }

        public string? GetNextMatch()
        {
            if (Matches.Count == 0) return null;
            CurrentIndex = (CurrentIndex + 1) % Matches.Count;
            return Matches[CurrentIndex];
        }
    }

    private InputLine? _inputLine;

    // What the input line and the full-screen state were when they were last reported, so a change is announced
    // once (the status bar shows them).
    private bool _reportedHasInputLine;

    private bool _reportedFullScreenProgramActive;
    private List<string> _commandHistory = new List<string>();
    private int _historyIndex = -1;
    private const int MaxHistorySize = 1000;
    private HistorySearchSession? _historySearch;
    private string _currentPrompt = "> ";
    private bool _autoDetectPrompt = true;
    private bool _localEcho = true;

    /// <summary>Raised when the user clicks a path or an URL shown in the terminal text. Device terminals run on this
    /// machine, so the path is a local file; the controller opens it with the default associated application.</summary>
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

    private bool IsLinkExtension(string extension)
    {
        string autoLinkExtensions = AppSettings?.VirtualTerminal?.AutoLinkExtensions;
        if (string.IsNullOrEmpty(autoLinkExtensions))
        {
            return false;
        }
        foreach (string item in autoLinkExtensions.Split(';'))
        {
            if (string.Equals(item, extension, StringComparison.OrdinalIgnoreCase) ||
                string.Equals("." + item, extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
                    itemsList.AddRange(GetBuiltInCommands());
                    // Local executables from PATH
                    itemsList.AddRange(GetLocalExecutables(filterText));
                }
                else if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
                {
                    // Local filesystem paths
                    _itemsDirectory = GetPathListingDirectory(filterText);
                    itemsList.AddRange(GetLocalPaths(filterText, _target == CompletionTarget.Directory));
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

            if (extracted.IndexOfAny(CompletionMetaChars) != -1)
            {
                return true;
            }
            if (ContainsUnescapedSpace(extracted) && extracted[0] != '\'' && extracted[0] != '"')
            {
                return true;
            }

            // For path/directory targets the listing depends on the directory the
            // token points into; when it changed we must re-enumerate.
            if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
            {
                string directory = GetPathListingDirectory(extracted.Trim('\'', '"'));
                if (!string.Equals(directory, _itemsDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    if (directory.Length == 0)
                    {
                        _items = new List<CompletionItem>();
                    }
                    else
                    {
                        LoadItems();
                    }
                }
            }

            UpdatePopup();
            return false;
        }

        private static readonly char[] CompletionMetaChars = { '`', '$', '<', '>', '|', '&', ';' };

        // Resolves the directory (with trailing separator) whose children would
        // be listed for the given path filter - mirrors GetLocalPaths so the
        // live-sync can detect when the listing context changed.
        private static string GetPathListingDirectory(string filter)
        {
            string path = filter;
            if (path.StartsWith("~"))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path.Substring(1);
            }

            int lastSlash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string directory = lastSlash >= 0
                ? path.Substring(0, lastSlash + 1)
                : Environment.CurrentDirectory + Path.DirectorySeparatorChar;

            if (!Directory.Exists(directory))
            {
                // Try to find parent directory
                DirectoryInfo di = new DirectoryInfo(directory);
                while (di != null && !di.Exists)
                {
                    di = di.Parent;
                }
                if (di != null)
                {
                    directory = di.FullName + Path.DirectorySeparatorChar;
                }
                else
                {
                    return "";
                }
            }
            return directory;
        }

        private static bool ContainsUnescapedSpace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ' ') continue;
                int backslashes = 0;
                for (int j = i - 1; j >= 0 && text[j] == '\\'; j--)
                {
                    backslashes++;
                }
                if (backslashes % 2 == 0)
                {
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<CompletionItem> GetBuiltInCommands()
        {
            // Built-in commands available in serial terminal
            string[] builtins = new[]
            {
                "help", "clear", "history", "exit", "quit",
                "cd", "pwd", "ls", "dir", "cat", "type",
                "echo", "set", "unset", "export", "env",
                "send", "sendhex", "sendfile", "recv", "recvfile",
                "break", "reset", "reconnect", "disconnect",
                "log", "logstart", "logstop", "baud", "settings"
            };

            foreach (string cmd in builtins)
            {
                bool hasDoc = BuiltInCommandDocs.TryGetValue(cmd, out (string Usage, string Description) doc);
                string usage = hasDoc ? doc.Usage : cmd;
                string description = hasDoc ? doc.Description : $"Built-in command: {cmd}";
                yield return new CompletionItem
                {
                    Icon = VirtualTerminalPopupItemIcon.Command,
                    Text = cmd,
                    InsertText = cmd,
                    Summary = new VirtualTerminalPopupSummary.OptionInfo
                    {
                        Option = cmd,
                        // Summary = argument signature, Info = short description; the completion
                        // popup tooltip shows both, giving the user argument hints.
                        Description = new CommandCompletion.OptionDescription { Summary = usage, Info = description }
                    }
                };
            }
        }

        /// <summary>Argument signatures and short descriptions shown as hints for the built-in commands.</summary>
        private static readonly Dictionary<string, (string Usage, string Description)> BuiltInCommandDocs =
            new Dictionary<string, (string Usage, string Description)>(StringComparer.Ordinal)
            {
                ["help"] = ("help", "Show available built-in commands"),
                ["clear"] = ("clear", "Clear the terminal screen"),
                ["history"] = ("history", "Show the command history"),
                ["exit"] = ("exit", "End the session"),
                ["quit"] = ("quit", "End the session"),
                ["cd"] = ("cd <directory>", "Change the current directory"),
                ["pwd"] = ("pwd", "Print the current directory"),
                ["ls"] = ("ls [directory]", "List directory contents"),
                ["dir"] = ("dir [directory]", "List directory contents"),
                ["cat"] = ("cat <file>", "Print the contents of a file"),
                ["type"] = ("type <file>", "Print the contents of a file"),
                ["echo"] = ("echo <text>", "Print text to the terminal"),
                ["set"] = ("set <name>=<value>", "Set a session variable"),
                ["unset"] = ("unset <name>", "Remove a session variable"),
                ["export"] = ("export <name>=<value>", "Export a session variable"),
                ["env"] = ("env", "Show all session variables"),
                ["send"] = ("send <text>", "Send text to the device"),
                ["sendhex"] = ("sendhex <hex>", "Send hexadecimal bytes to the device"),
                ["sendfile"] = ("sendfile <file>", "Send the contents of a file to the device"),
                ["recv"] = ("recv <length>", "Receive a number of bytes from the device"),
                ["recvfile"] = ("recvfile <file>", "Receive bytes from the device into a file"),
                ["break"] = ("break", "Send a serial break condition"),
                ["reset"] = ("reset", "Reset the device connection"),
                ["reconnect"] = ("reconnect", "Reconnect to the device"),
                ["disconnect"] = ("disconnect", "Disconnect from the device"),
                ["log"] = ("log <file>", "Log terminal output to a file"),
                ["logstart"] = ("logstart <file>", "Start logging terminal output to a file"),
                ["logstop"] = ("logstop", "Stop logging terminal output"),
                ["baud"] = ("baud <rate>", "Set the baud rate, e.g. 115200"),
                ["settings"] = ("settings", "Open the terminal settings")
            };

        private IEnumerable<CompletionItem> GetLocalExecutables(string filter)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] pathDirs = pathEnv.Split(Path.PathSeparator);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in pathDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string file in EnumerateFilesSafe(dir))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (seen.Add(name) && (string.IsNullOrEmpty(filter) || name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return new CompletionItem
                        {
                            Icon = VirtualTerminalPopupItemIcon.Command,
                            Text = name,
                            InsertText = name,
                            Summary = new VirtualTerminalPopupSummary.PathInfo { FullPath = file }
                        };
                    }
                }
            }
        }

        private IEnumerable<CompletionItem> GetLocalPaths(string filter, bool directoriesOnly)
        {
            string path = filter;
            string directory;
            string prefix;

            // Handle relative paths
            if (path.StartsWith("~"))
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path.Substring(1);
            }

            int lastSlash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            if (lastSlash >= 0)
            {
                directory = path.Substring(0, lastSlash + 1);
                prefix = path.Substring(lastSlash + 1);
            }
            else
            {
                directory = Environment.CurrentDirectory + Path.DirectorySeparatorChar;
                prefix = path;
            }

            if (!Directory.Exists(directory))
            {
                // Try to find parent directory
                DirectoryInfo di = new DirectoryInfo(directory);
                while (di != null && !di.Exists)
                {
                    di = di.Parent;
                }
                if (di != null)
                {
                    directory = di.FullName + Path.DirectorySeparatorChar;
                }
                else
                {
                    yield break;
                }
            }

            if (directoriesOnly)
            {
                foreach (string dir in EnumerateDirectoriesSafe(directory))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new CompletionItem
                        {
                            Icon = VirtualTerminalPopupItemIcon.Directory,
                            Text = name,
                            InsertText = name + Path.DirectorySeparatorChar,
                            Summary = new VirtualTerminalPopupSummary.FileOrDirectoryInfo
                            {
                                FullPath = dir,
                                IsDirectory = true
                            }
                        };
                    }
                }
            }
            else
            {
                // Directories first
                foreach (string dir in EnumerateDirectoriesSafe(directory))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new CompletionItem
                        {
                            Icon = VirtualTerminalPopupItemIcon.Directory,
                            Text = name,
                            InsertText = name + Path.DirectorySeparatorChar,
                            Summary = new VirtualTerminalPopupSummary.FileOrDirectoryInfo
                            {
                                FullPath = dir,
                                IsDirectory = true
                            }
                        };
                    }
                }
                // Then files
                foreach (string file in EnumerateFilesSafe(directory))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new CompletionItem
                        {
                            Icon = VirtualTerminalPopupItemIcon.File,
                            Text = name,
                            InsertText = name,
                            Summary = new VirtualTerminalPopupSummary.FileOrDirectoryInfo
                            {
                                FullPath = file,
                                IsDirectory = false
                            }
                        };
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateFilesSafe(string directory)
        {
            try
            {
                return Directory.GetFiles(directory);
            }
            catch
            {
                return new string[0];
            }
        }

        private static IEnumerable<string> EnumerateDirectoriesSafe(string directory)
        {
            try
            {
                return Directory.GetDirectories(directory);
            }
            catch
            {
                return new string[0];
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
            if (_items == null) return;

            string tokenText = GetTokenText();
            string filter = tokenText.Trim('\'', '"');

            var filtered = _items.Where(item => item.Text.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            _popup.SetItemList(filtered);
            _popup.SetFilter(filter);
        }

        private void ApplyBestMatch()
        {
            string tokenText = GetTokenText();
            string filter = tokenText.Trim('\'', '"');

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
                    common = CommonPrefix(common, item.Text);
                }
                if (common.Length > 0 && common != filter)
                {
                    ApplyItem(new CompletionItem { Text = common, InsertText = common }, commit: false);
                }
            }
        }

        private static string CommonPrefix(string first, string second)
        {
            int limit = Math.Min(first.Length, second.Length);
            for (int i = 0; i < limit; i++)
            {
                if (first[i] != second[i])
                {
                    return first.Substring(0, i);
                }
            }
            return first.Substring(0, limit);
        }

        private void OnItemSelected(CompletionItem item, Keys keys)
        {
            ApplyItem(item, keys == Keys.Return);
        }

        private void ApplyItem(CompletionItem item, bool commit, string suffix = "")
        {
            if (item.InsertText == null) return;

            string insertText = item.InsertText;
            bool isDir = insertText.EndsWith(Path.DirectorySeparatorChar.ToString());

            if (isDir)
            {
                if (suffix == Path.DirectorySeparatorChar.ToString()) suffix = "";
                else if (commit) insertText = insertText.TrimEnd(Path.DirectorySeparatorChar);
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
            _popup?.Close();
        }
    }

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
    public bool SyncRemoteSize { get; set; }

    // The size the device was told last (empty: not told on this connection yet).
    private Size _announcedSize = Size.Empty;

    // The device has shown the prompt of a Linux shell ("# " or "$ ") and not another kind of prompt since.
    private bool _shellPromptSeen;

    /// <summary>True when "Send terminal size now" can run: the input line is shown and nothing is typed in it.</summary>
    public bool CanSendRemoteSize => _inputLine != null && _inputLine.Text.Length == 0 && LineInputText.BuildRemoteSizeCommand(TerminalSize.Width, TerminalSize.Height) != null;

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

    public IReadOnlyList<string> CommandHistory => _commandHistory.AsReadOnly();

    /// <summary>
    /// When true, the characters typed while no input line is shown (a login prompt, a program that reads single
    /// characters) are also drawn by the terminal itself, with Enter and Backspace; for connections whose far end does
    /// not echo (telnet without the server's echo, line mode). Typing in the input line needs no echo.
    /// </summary>
    public bool EchoRawInput { get; set; }

    // How many typed characters of the current line Backspace may still erase on the screen.
    private int _rawEchoLength;

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
        _commandHistory.Clear();
        _historyIndex = -1;
    }

    public void AddToHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        string trimmed = command.Trim();
        if (_commandHistory.Count > 0 && _commandHistory[_commandHistory.Count - 1] == trimmed)
            return; // Don't add duplicates

        _commandHistory.Add(trimmed);
        if (_commandHistory.Count > MaxHistorySize)
        {
            _commandHistory.RemoveAt(0);
        }
        _historyIndex = _commandHistory.Count;
    }

    /// <summary>
    /// The device has just printed its prompt: the input line continues that very line after the device's cursor,
    /// instead of a second, local prompt on a line of its own ("root@OpenWrt:/# " followed by "# "). False when the
    /// cursor is not at the end of a prompt (something follows it, or the text before it does not end like one).
    /// </summary>
    private bool ContinueDevicePrompt()
    {
        if (ActiveBufferIndex != 0 || _visibleColumns <= 0)
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
        _inputLine = new InputLine(cursor.Line, prompt.Length, prompt);
        RewrapInput();
        OnTextReplaced();
        UpdateCaretFromInputLine();
        NavigationLocked = false;
        _historyIndex = _commandHistory.Count;
        _historySearch = null;
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
        _inputLine = new InputLine(startLine, startColumn, _currentPrompt);
        ((EditableTextBrowser)this).EditableDocument.AppendLine(_currentPrompt);
        RewrapInput();
        OnTextReplaced();
        SetCaret(((EditableTextBrowser)this).EditableDocument.EndPosition);
        NavigationLocked = false;
        _historyIndex = _commandHistory.Count;
        _historySearch = null;
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

    private void UpdateCaretFromInputLine()
    {
        if (_inputLine == null) return;
        SetCaret(GetCaretPositionFromInputIndex(_inputLine.CaretIndex));
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
        if (_completionPopup != null && _completionPopup.TryHandleKeyEvent(e))
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
            if (_historySearch != null)
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
        else if (tokens.Length > 0)
        {
            // Caret is after the last token
            var lastToken = tokens[tokens.Length - 1];
            tokenStart = lastToken.End;
            tokenEnd = lastToken.End;

            string prevText = lastToken.Extract(text);
            if (prevText == "cd" || prevText == "ls" || prevText == "dir")
            {
                target = CompletionTarget.Directory;
            }
            else
            {
                target = CompletionTarget.Path;
            }
        }

        // Request completion popup
        var request = new CompletionPopupRequest
        {
            ScreenLocation = PointToScreen(Point.Round(PositionToPoint(CaretPosition.Column, CaretPosition.Line))),
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

        if (_historySearch == null)
        {
            string currentText = _inputLine.Text;
            _historySearch = new HistorySearchSession(_commandHistory, currentText, currentText);
        }
        string? match = _historySearch.GetNextMatch();
        if (match != null)
        {
            _inputLine.SetText(match);
            RewrapInput();
            UpdateCaretFromInputLine();
        }
    }

    private void CancelHistorySearch()
    {
        if (_historySearch != null && _inputLine != null)
        {
            _inputLine.SetText(_historySearch.OriginalInput);
            RewrapInput();
            UpdateCaretFromInputLine();
            _historySearch = null;
        }
    }

    private void NavigateHistory(bool forward)
    {
        if (_inputLine == null || _commandHistory.Count == 0) return;

        // Cancel any active history search
        if (_historySearch != null)
        {
            _historySearch = null;
        }

        if (!forward) // Up arrow - go back in history
        {
            if (_historyIndex > 0)
            {
                _historyIndex--;
                _inputLine.SetText(_commandHistory[_historyIndex]);
                RewrapInput();
                UpdateCaretFromInputLine();
                // Send the updated line to device for echo
                if (_localEcho)
                {
                    SendLineToDevice();
                }
            }
        }
        else // Down arrow - go forward in history
        {
            if (_historyIndex < _commandHistory.Count - 1)
            {
                _historyIndex++;
                _inputLine.SetText(_commandHistory[_historyIndex]);
                RewrapInput();
                UpdateCaretFromInputLine();
                // Send the updated line to device for echo
                if (_localEcho)
                {
                    SendLineToDevice();
                }
            }
            else if (_historyIndex == _commandHistory.Count - 1)
            {
                _historyIndex = _commandHistory.Count;
                _inputLine.SetText("");
                RewrapInput();
                UpdateCaretFromInputLine();
                // Send the updated line to device for echo
                if (_localEcho)
                {
                    SendLineToDevice();
                }
            }
        }
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
    private void SubmitCommand(string commandOverride)
    {
        if (_inputLine == null) return;

        bool typedByUser = commandOverride == null;
        string command = (commandOverride ?? _inputLine.Text).Trim();
        _historySearch = null;

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
        string lineEnding = EnterSequence ?? "\r";
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

        _historySearch = null;
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

        _historySearch = null;
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
        if (_inputLine != null)
        {
            // Constrain caret to input area
            if (textPosition < _inputLine.StartPosition)
            {
                textPosition = _inputLine.StartPosition;
            }
        }
        base.SetCaretCore(textPosition, selectionChangeSource, caretDirection);
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
        _historySearch = null;
        _historyIndex = _commandHistory.Count;
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

    public override void PasteText(string clipboardText)
    {
        if (UsesTerminalInput)
        {
            base.PasteText(clipboardText);
            return;
        }
        try
        {
            string text = PasteGuard.ApplyMode(clipboardText, PasteModeInUse);
            if (!string.IsNullOrEmpty(text))
            {
                // Each line is sent as a command: a command wrapped by its source must not go out in pieces.
                text = ShellCommandLine.JoinWrappedLines(text, indentContinues: false);
            }
            // Every line break submits the line to the device at once.
            text = _inputLine != null ? PasteGuard.Prepare(text, AppSettings, linesRunAtOnce: true, owner: this) : null;
            // The device may use an encoding without some of the characters of the text.
            text = text != null ? PasteGuard.FitToEncoding(text, InputEncoding, AppSettings, this) : null;
            if (_inputLine != null && !string.IsNullOrEmpty(text))
            {
                int offset = _inputLine.CaretIndex;
                var run = new System.Text.StringBuilder();
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        if (run.Length > 0)
                        {
                            ReplaceInputLineText(offset, 0, run.ToString());
                            offset += run.Length;
                            run.Clear();
                        }
                        SubmitCommand();
                        if (_inputLine == null)
                        {
                            // Text after a final newline falls on a fresh prompt that
                            // has not been drawn yet; stop inserting (same as before).
                            break;
                        }
                        offset = 0;
                    }
                    else
                    {
                        run.Append(ch);
                    }
                }
                if (run.Length > 0)
                {
                    ReplaceInputLineText(offset, 0, run.ToString());
                }
                UpdateCaretFromInputLine();
                // Send pasted text to device
                if (_localEcho)
                {
                    SendText(text, InputKind.Paste);
                }
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
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

    private enum LinkCandidateState
    {
        Word,
        Rejected,
        AbsolutePath,
        Position
    }

    private class LocalLinkCandidate : ILinkTarget
    {
        // ':' and '\\' are valid here too: drive-letter paths (C:\...) and URLs with ports stay in one candidate.
        internal static readonly string PathCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-/:\\";

        public LinkCandidateState State;

        private readonly SerialTerminalControl _terminal;

        private readonly string _baseDirectory;

        private readonly StringBuilder _path = new StringBuilder();

        private StringBuilder _extension;

        public LocalLinkCandidate(SerialTerminalControl terminal, char firstChar)
        {
            _terminal = terminal;
            _baseDirectory = Environment.CurrentDirectory;
            _path.Append(firstChar);
            State = (firstChar == '\\' || firstChar == '/') ? LinkCandidateState.AbsolutePath : LinkCandidateState.Word;
        }

        public bool IsActive
        {
            get
            {
                if (State != LinkCandidateState.Position)
                {
                    return false;
                }
                string path = ComputeTargetPath();
                if (path == null)
                {
                    return false;
                }
                if (IsUrl(path))
                {
                    return true;
                }
                try
                {
                    return File.Exists(path) || Directory.Exists(path);
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Activate()
        {
            string path = ComputeTargetPath();
            if (path != null)
            {
                _terminal.RequestOpenFile(path);
            }
        }

        private static bool IsUrl(string path)
        {
            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        }

        private string ComputeTargetPath()
        {
            try
            {
                string path = _path.ToString();
                if (IsUrl(path))
                {
                    return path;
                }
                bool isDosDrive = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
                if (State == LinkCandidateState.AbsolutePath || isDosDrive)
                {
                    return Path.GetFullPath(path);
                }
                return Path.GetFullPath(Path.Combine(_baseDirectory, path));
            }
            catch
            {
                return null;
            }
        }

        public bool Accept(char ch)
        {
            if (State == LinkCandidateState.Rejected || State == LinkCandidateState.Position)
            {
                return false;
            }
            if (ch == '.')
            {
                _extension = new StringBuilder();
            }
            if (PathCharacters.IndexOf(ch) >= 0)
            {
                _path.Append(ch);
                _extension?.Append(ch);
                return true;
            }
            Complete();
            return false;
        }

        public void Complete()
        {
            if (State == LinkCandidateState.Rejected || State == LinkCandidateState.Position)
            {
                return;
            }
            string path = _path.ToString();
            bool isDosDrive = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
            if (IsUrl(path) || State == LinkCandidateState.AbsolutePath || isDosDrive)
            {
                State = LinkCandidateState.Position;
            }
            else if (_extension != null && _terminal.IsLinkExtension(_extension.ToString()))
            {
                State = LinkCandidateState.Position;
            }
            else
            {
                State = LinkCandidateState.Rejected;
            }
        }
    }

    private class LocalLinkScanner : ILinkScanner
    {
        private readonly SerialTerminalControl _terminal;

        private LocalLinkCandidate _current;

        private char _previousChar;

        public LocalLinkScanner(SerialTerminalControl terminal)
        {
            _terminal = terminal;
        }

        private LocalLinkCandidate ReadChar(char ch)
        {
            char prevChar = _previousChar;
            _previousChar = ch;
            if (_current == null)
            {
                if (LocalLinkCandidate.PathCharacters.IndexOf(ch) >= 0 && prevChar != '\\')
                {
                    return _current = new LocalLinkCandidate(_terminal, ch);
                }
                return null;
            }
            if (!_current.Accept(ch))
            {
                _current = null;
            }
            return _current;
        }

        public LinkCell[] Scan(string lineText)
        {
            char[] charArray = lineText.ToArray();
            LocalLinkCandidate[] candidateArray = charArray.Select(ReadChar).ToArray();
            for (int i = 0; i < candidateArray.Length; i++)
            {
                if (candidateArray[i] != null && candidateArray[i].State == LinkCandidateState.Rejected)
                {
                    candidateArray[i] = null;
                }
            }
            return candidateArray.Select((LocalLinkCandidate linkCandidate) => new LinkCell
            {
                Target = linkCandidate
            }).ToArray();
        }

        public void Finish()
        {
            _current?.Complete();
        }
    }
}