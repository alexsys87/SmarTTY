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

namespace SmarTTY.Controls;

public class SerialTerminalControl : TerminalEmulatorControl
{
    private class InputLine
    {
        public readonly int StartLine;
        public readonly int StartColumn;
        public readonly string Prompt;
        private string _text = "";
        private int _caretIndex = 0;
        private readonly SerialTerminalControl _terminal;

        public string Text => _text;
        public int CaretIndex => _caretIndex;
        public TextDocument.TextPosition StartPosition => new TextDocument.TextPosition(StartColumn, StartLine);

        public InputLine(SerialTerminalControl terminal, int startLine, int startColumn, string prompt)
        {
            _terminal = terminal;
            StartLine = startLine;
            StartColumn = startColumn;
            Prompt = prompt;
        }

        public void SetText(string text)
        {
            _text = text ?? "";
            _caretIndex = _text.Length;
        }

        public void InsertChar(char ch)
        {
            _text = _text.Insert(_caretIndex, ch.ToString());
            _caretIndex++;
        }

        public void RemoveCharBackward()
        {
            if (_caretIndex > 0)
            {
                _text = _text.Remove(_caretIndex - 1, 1);
                _caretIndex--;
            }
        }

        public void RemoveCharForward()
        {
            if (_caretIndex < _text.Length)
            {
                _text = _text.Remove(_caretIndex, 1);
            }
        }

        public void MoveCaretLeft()
        {
            if (_caretIndex > 0) _caretIndex--;
        }

        public void MoveCaretRight()
        {
            if (_caretIndex < _text.Length) _caretIndex++;
        }

        public void MoveCaretHome()
        {
            _caretIndex = 0;
        }

        public void MoveCaretEnd()
        {
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

        public string? GetPreviousMatch()
        {
            if (Matches.Count == 0) return null;
            CurrentIndex = (CurrentIndex - 1 + Matches.Count) % Matches.Count;
            return Matches[CurrentIndex];
        }
    }

    private InputLine? _inputLine;
    private List<string> _commandHistory = new List<string>();
    private int _historyIndex = -1;
    private const int MaxHistorySize = 1000;
    private HistorySearchSession? _historySearch;
    private string _defaultPrompt = "> ";
    private string _currentPrompt = "> ";
    private bool _autoDetectPrompt = true;
    private bool _localEcho = true;

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
        private readonly string _commandText;
        private readonly int _tokenStart;
        private readonly int _tokenEnd;
        private readonly ICompletionPopup _popup;
        private readonly bool _autoApply;
        private List<CompletionItem> _items;
        private bool _isLoading;

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

                if (_target == CompletionTarget.Command && string.IsNullOrEmpty(filterText))
                {
                    // Built-in local commands
                    itemsList.AddRange(GetBuiltInCommands());
                    // Local executables from PATH
                    itemsList.AddRange(GetLocalExecutables(filterText));
                }
                else if (_target == CompletionTarget.Path || _target == CompletionTarget.Directory)
                {
                    // Local filesystem paths
                    itemsList.AddRange(GetLocalPaths(filterText, _target == CompletionTarget.Directory));
                }

                _items = itemsList;
                _isLoading = false;
                _terminal.BeginInvoke((Action)(() => OnItemsLoaded()));
            }
            catch (Exception ex)
            {
                _popup.DisplayException(ex);
                _isLoading = false;
            }
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
                yield return new CompletionItem
                {
                    Icon = VirtualTerminalPopupItemIcon.Command,
                    Text = cmd,
                    InsertText = cmd,
                    Summary = new VirtualTerminalPopupSummary.OptionInfo
                    {
                        Option = cmd,
                        Description = new CommandCompletion.OptionDescription { Summary = $"Built-in command: {cmd}" }
                    }
                };
            }
        }

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
            _isLoading = false;
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

    public SerialTerminalControl()
    {
    }

    protected override bool UsesTerminalInput => _inputLine != null;

    public string CurrentPrompt => _currentPrompt;

    public IReadOnlyList<string> CommandHistory => _commandHistory.AsReadOnly();

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
        _inputLine = new InputLine(this, startLine, startColumn, _currentPrompt);
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

        ReplaceTextCore(
            new TextDocument.TextPosition(0, _inputLine.StartLine),
            new TextDocument.TextPosition(0, ((EditableTextBrowser)this).EditableDocument.LineCount),
            select: false,
            wrapped.ToString());

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

        // Handle history search (Ctrl+R)
        if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.R)
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
            base.OnKeyPress(e);
            return;
        }

        // Handle printable characters
        if (!char.IsControl(e.KeyChar))
        {
            _inputLine.InsertChar(e.KeyChar);
            RewrapInput();
            UpdateCaretFromInputLine();
            
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
                _inputLine.RemoveCharBackward();
                RewrapInput();
                UpdateCaretFromInputLine();
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

    private void HandleHistorySearch()
    {
        if (_inputLine == null) return;

        string currentText = _inputLine.Text;

        if (_historySearch == null)
        {
            // Start new search
            _historySearch = new HistorySearchSession(_commandHistory, currentText, currentText);
            string? match = _historySearch.GetNextMatch();
            if (match != null)
            {
                _inputLine.SetText(match);
                RewrapInput();
                UpdateCaretFromInputLine();
                ProcessOutput($"(reverse-i-search)`{currentText}': {match}");
            }
            else
            {
                ProcessOutput($"(reverse-i-search)`{currentText}': {currentText}");
            }
        }
        else
        {
            // Continue search with same term
            string? match = _historySearch.GetNextMatch();
            if (match != null)
            {
                _inputLine.SetText(match);
                RewrapInput();
                UpdateCaretFromInputLine();
                ProcessOutput($"(reverse-i-search)`{_historySearch.OriginalInput}': {match}");
            }
            else
            {
                ProcessOutput($"(reverse-i-search)`{_historySearch.OriginalInput}': {_historySearch.OriginalInput}");
            }
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
            ProcessOutput($"{_currentPrompt}{_historySearch.OriginalInput}");
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
        
        // Send the full line (prompt + text) to device
        string fullLine = _inputLine.Prompt + _inputLine.Text;
        SendText(fullLine, InputKind.Typed);
    }

    private void SubmitCommand()
    {
        if (_inputLine == null) return;

        string command = _inputLine.Text.Trim();
        _historySearch = null;

        // Add to history
        AddToHistory(command);

        // Move to next line and show command output
        ((EditableTextBrowser)this).EditableDocument.AppendLine("");
        NavigationLocked = true;
        ActiveBuffer.FirstRow = ((EditableTextBrowser)this).EditableDocument.LineCount - 1;
        base.CursorPosition = new Point(0, 0);
        UpdateCaret();
        UpdateLayout();

        // Send command to device
        string lineEnding = EnterSequence ?? "\r";
        SendText(command + lineEnding, InputKind.Typed);

        // Reset input line
        _inputLine = null;
    }

    private void ClearCurrentLine()
    {
        if (_inputLine == null) return;

        _historySearch = null;
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

        string currentText = _inputLine.Text;
        _historySearch = null;

        Reset();
        _inputLine = new InputLine(this, 0, 0, _currentPrompt);
        _inputLine.SetText(currentText);
        ((EditableTextBrowser)this).EditableDocument.AppendLine(_currentPrompt + currentText);
        RewrapInput();
        UpdateCaretFromInputLine();
        // Send clear screen escape sequence to device
        SendText("\u001b[2J\u001b[H", InputKind.KeySequence);
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

    public override void BeginNewSession()
    {
        base.BeginNewSession();
        _inputLine = null;
        ShowPrompt();
    }

    public override void Reset()
    {
        base.Reset();
        _inputLine = null;
        _historySearch = null;
        _historyIndex = _commandHistory.Count;
        ShowPrompt();
    }

    public override void Paste()
    {
        if (!UsesTerminalInput)
        {
            base.Paste();
            return;
        }
        try
        {
            string text = Win32Clipboard.GetText(Handle);
            if (text != null)
            {
                text = text.Replace("\r", "");
            }
            if (_inputLine != null && !string.IsNullOrEmpty(text))
            {
                foreach (char ch in text)
                {
                    if (ch == '\n')
                    {
                        SubmitCommand();
                    }
                    else
                    {
                        _inputLine.InsertChar(ch);
                    }
                }
                RewrapInput();
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
            MessageBox.Show(exception.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    public void OnDataReceived(string text)
    {
        // Auto-detect prompt from received data
        if (_autoDetectPrompt && _inputLine == null)
        {
            DetectPrompt(text);
        }

        ProcessOutput(text);

        // If we're at a prompt, show new prompt
        if (_inputLine == null && !string.IsNullOrEmpty(text))
        {
            // Check if the text ends with a prompt-like pattern
            if (text.TrimEnd().EndsWith(_currentPrompt) ||
                text.TrimEnd().EndsWith("> ") ||
                text.TrimEnd().EndsWith("# ") ||
                text.TrimEnd().EndsWith("$ ") ||
                text.TrimEnd().EndsWith(">> "))
            {
                ShowPrompt();
            }
        }
    }

    private void DetectPrompt(string text)
    {
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
}