using SmarTTY.Ssh;
using SmarTTY.IO;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Controls;

public class SshTerminalControl : TerminalEmulatorControl
{
    public class OpenFileEventArgs : EventArgs
    {
        public string Path;

        public int? Line;

        public int? Column;
    }

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

        public void NavigateHistory(bool forward)
        {
            string text = this.Text;
            if (_history == null)
            {
                _history = BuildHistory();
                _history.Remove(text ?? "");
                _history.Insert(0, text ?? "");
            }
            else if (_historyIndex >= 0 && _historyIndex < _history.Count && !string.IsNullOrEmpty(text))
            {
                _history[_historyIndex] = text;
            }
            if (forward)
            {
                if (++_historyIndex >= _history.Count)
                {
                    _historyIndex = 0;
                }
            }
            else if (--_historyIndex < 0)
            {
                _historyIndex = _history.Count - 1;
            }
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
            string[] items = _terminal._shellCompletionAnalyzer?.GetHistory();
            List<string> list = new List<string>();
            HashSet<string> hashSet = new HashSet<string>();
            if (items == null)
            {
                items = new string[0];
            }
            foreach (string item in items.Concat(_terminal._typedCommands).Reverse())
            {
                if (!hashSet.Contains(item))
                {
                    list.Add(item);
                    hashSet.Add(item);
                }
            }
            return list;
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
            _channel.Input.Write(buffer, 0, buffer.Length);
        }

        public void Resize(int columns, int rows)
        {
            _channel.ResizePty(columns, rows);
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

    private string _currentDirectory;

    private CompletionSession _completion;

    private List<string> _typedCommands = new List<string>();

    private Dictionary<string, string> _environment = new Dictionary<string, string>();

    private List<CommandRun> _runs = new List<CommandRun>();

    private Queue<Action> _uiQueue = new Queue<Action>();

    internal readonly ShellCompletionAnalyzer _shellCompletionAnalyzer;

    private ICompletionPopup _popup;

    private string PromptPrefix
    {
        get
        {
            string text = _currentDirectory ?? "";
            if (text.StartsWith(_homeDirectory))
            {
                text = "~" + text.Substring(_homeDirectory.Length);
            }
            return _context.TitleWithUserName + ":" + text;
        }
    }

    protected virtual bool IsCommandRunning => _inputLine == null;

    // At the prompt (_inputLine != null) the user edits the local input line, so keys must NOT be
    // forwarded to the remote channel; only while a command is running do they go to the terminal.
    protected override bool UsesTerminalInput => _inputLine == null;

    protected virtual bool HasInputLine => _inputLine != null;

    public string CurrentDirectory => _currentDirectory;

    public SshSession Session => _context.Session;

    public bool HasEnvironment => _environment.Count > 0;

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
        UpdateTitle();
        TerminalSizeChanged += OnTerminalSizeChanged;
        base.DataEntered += OnDataEntered;
        _shellCompletionAnalyzer = terminalSessionContext.ProvideAnalyzer();
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

    public void ChangeDirectory(string path)
    {
        if (path.StartsWith("~"))
        {
            path = _homeDirectory + path.Substring(1);
        }
        path = ((!path.StartsWith("\"") && !path.StartsWith("'")) ? path.Replace("\\ ", " ") : path.Trim('\'', '"'));
        string text = RemotePathUtils.NormalizeUnix(RemotePathUtils.Combine(_currentDirectory, path));
        string trimmedPath = text.TrimEnd('/');
        if (trimmedPath.Length > 0)
        {
            text = trimmedPath;
        }
        List<string> list = _context.Session.RunCommand("cd \"" + text + "\"", out var exitCode) ?? new List<string>();
        foreach (string item in list)
        {
            ProcessOutput(item);
        }
        if (exitCode == 0)
        {
            _currentDirectory = text;
            DirectoryChanged?.Invoke(text);
        }
        else if (list.Count == 0)
        {
            ProcessOutput(TerminalEmulatorControl.Colorize($"'cd {text}' exited with code {exitCode ?? (-1)}", 1));
        }
        ShowPrompt();
    }

    private void ShowPrompt()
    {
        ActiveBufferIndex = 0;
        if (((EditableTextBrowser)this).EditableDocument.LineCount > 0 && ((EditableTextBrowser)this).EditableDocument.GetLineText(((EditableTextBrowser)this).EditableDocument.LineCount - 1).Trim() == "")
        {
            ((EditableTextBrowser)this).EditableDocument.RemoveLines(((EditableTextBrowser)this).EditableDocument.LineCount - 1, 1);
        }
        char ch = '$';
        if (_context.IsRoot)
        {
            ch = '#';
        }
        _inputLine = new InputLine(this, ((EditableTextBrowser)this).EditableDocument.LineCount, $"{PromptPrefix}{ch} ");
        ((EditableTextBrowser)this).EditableDocument.AppendLine(_inputLine.Prompt);
        RewrapInput();
        OnTextReplaced();
        SetCaret(((EditableTextBrowser)this).EditableDocument.EndPosition);
        NavigationLocked = false;
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

    private bool InputWraps()
    {
        if (_inputLine == null)
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
            if (completionSession != null && completionSession.OnTextChanged(filter, startIndex, endIndex - startIndex, (text ?? "").Replace("\n", "").Replace("\r", "").Length))
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
            if (text != null && index >= 0 && index < text.Length)
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

    protected override void AdjustSelectionEnd(ref TextDocument.TextPosition textPosition)
    {
        if (_inputLine != null && ((ReadonlyTextBrowser)this).HasSelection && !(SelectionEnd < _inputLine.StartPosition) && textPosition < _inputLine.StartPosition)
        {
            textPosition = _inputLine.StartPosition;
        }
        base.AdjustSelectionEnd(ref textPosition);
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
        string text = inputLine.Text;
        int caretIndex = inputLine.Region.CaretIndex;
        CommandCompletion commandCompletion = _shellCompletionAnalyzer.CompletionDatabase?.LookupCommand(text);
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
        CompletionSession.Token[] items = CompletionSession.Tokenize(text);
        int? matchIndex = Enumerable.Range(0, items.Length).Select((Func<int, int?>)((int value) => value)).FirstOrDefault((int? itemIndex) => items[itemIndex.Value].Contains(caretIndex));
        int? previousIndex = null;
        if (matchIndex.HasValue)
        {
            previousIndex = matchIndex.Value - 1;
        }
        else if (items.Length != 0)
        {
            previousIndex = items.Length - 1;
        }
        CompletionSession.Token token = default;
        if (matchIndex.HasValue)
        {
            token = items[matchIndex.Value];
        }
        CompletionTarget completionTarget;
        if (items.Length == 0 || matchIndex == 0 || (previousIndex.HasValue && items[previousIndex.Value].Text == "|"))
        {
            completionTarget = CompletionTarget.Command;
        }
        else
        {
            completionTarget = ((items.Length == 0 || !(items[0].Text == "cd")) ? CompletionTarget.Path : CompletionTarget.Directory);
            if (token.Text == null)
            {
                token = new CompletionSession.Token(text, text.Length, text.Length);
            }
        }
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
            _completion = new CompletionSession(this, text, completionTarget, token, completionPopupRequest.Popup, completionTrigger == CompletionTrigger.Tab, commandCompletion);
            _completion.ItemApplied += OnCompletionApplied;
        }
    }

    private void OnCompletionApplied(object sender, CompletionSession.AppliedEventArgs item)
    {
        InputRegion inputRegion = _inputLine?.Region;
        if (inputRegion != null)
        {
            inputRegion.Text = item.NewText;
            inputRegion.CaretIndex = item.CaretIndex;
        }
        if (item.Commit)
        {
            SubmitInput();
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        bool? isAtWordStart = null;
        if (_inputLine != null)
        {
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
        CompletionSession completionSession = _completion;
        ICompletionPopup completionPopup = _popup;
        if ((completionPopup != null && completionPopup.TryHandleKeyEvent(e)) || (completionSession != null && completionSession.HandleKey(e)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CompletionSession.AppliedEventArgs appliedEventArgs = completionSession?.LastApplied;
            if (appliedEventArgs != null && appliedEventArgs.IsDirectory && e.KeyCode != Keys.Return)
            {
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
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                switch (e.KeyCode)
                {
                    case Keys.L:
                        if (_inputLine != null)
                        {
                            string text = _inputLine.Text;
                            Reset();
                            ShowPrompt();
                            _inputLine?.SetText(text);
                        }
                        break;
                    case Keys.C:
                        _inputLine?.SetText("");
                        break;
                    case Keys.D:
                        EndOfInputRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case Keys.R:
                        {
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
                            _popup = completionPopupRequest.Popup;
                            if (completionPopupRequest.Popup != null)
                            {
                                completionPopupRequest.Popup.ItemSelected += (CompletionItem completionItem, Keys keys) =>
                                {
                                    _inputLine.SetText(completionItem.Text);
                                    if (keys == Keys.Return)
                                    {
                                        SubmitInput();
                                    }
                                };
                            }
                            _popup?.SetFilter(_inputLine?.Text);
                            break;
                        }
                    case Keys.Space:
                        if (CompletionTriggers().CtrlSpace)
                        {
                            ShowCompletions();
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        break;
                }
            }
            else
            {
                Keys keyCode = e.KeyCode;
                if (keyCode == Keys.Up || keyCode == Keys.Down)
                {
                    _inputLine.NavigateHistory(e.KeyCode == Keys.Up);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
        }
        base.OnKeyDown(e);
    }

    private void BeginExecution()
    {
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
        string text = "cd \"" + _currentDirectory + "\" && ";
        interactive = interactive ?? _context.VTSettings.InteractiveModeWrapper;
        foreach (KeyValuePair<string, string> item in _environment)
        {
            command = item.Key + "=" + item.Value + " " + command;
        }
        if (interactive.Value)
        {
            command = _context.VTSettings.DefaultShell + " -i -c \"" + ShellCompletionAnalyzer.EscapeForShell(command) + "\"";
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
        if (string.IsNullOrEmpty(command))
        {
            ShowPrompt();
            return true;
        }
        BeginExecution();
        try
        {
            _typedCommands.Add(command);
            _shellCompletionAnalyzer.AppendToHistory(command);
            if (command.StartsWith("cd "))
            {
                string text = command.Substring(2).Trim();
                ChangeDirectory(text);
            }
            else if (command.StartsWith("export "))
            {
                string assignment = command.Substring(6).Trim();
                int equalsIndex = assignment.IndexOf('=');
                if (equalsIndex != -1)
                {
                    string key = assignment.Substring(0, equalsIndex);
                    if (equalsIndex == assignment.Length - 1)
                    {
                        _environment.Remove(key);
                    }
                    else
                    {
                        _environment[key] = assignment.Substring(equalsIndex + 1);
                    }
                }
                ShowPrompt();
            }
            else
            {
                SshShellChannel sshShellChannel = _context.Session.OpenShellChannel(BuildRemoteCommand(command), "xterm", ExtendedDataMode.Merge);
                if (sshShellChannel == null)
                {
                    throw new Exception("Failed to open an SSH channel");
                }
                CommandRun item = new CommandRun(this, command, _currentDirectory, ((EditableTextBrowser)this).EditableDocument.LineCount - 1);
                _runs.Add(item);
                CommandChannel commandChannel = new CommandChannel(sshShellChannel, this, item);
                _channel = commandChannel;
                sshShellChannel.Finished += (object sender) =>
                {
                    OnCommandFinished(command, commandChannel);
                };
                sshShellChannel.PollingEnabled = true;
                sshShellChannel.ResizePty(TerminalSize.Width, TerminalSize.Height);
            }
        }
        catch (Exception exception)
        {
            ProcessOutput(TerminalEmulatorControl.Colorize(exception.Message, 1));
            ShowPrompt();
            return true;
        }
        return true;
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
        if (queueWasEmpty)
        {
            BeginInvoke(new Action(DrainUiQueue));
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
            try
            {
                action();
            }
            catch
            {
            }
        }
    }

    private void OnCommandFinished(string text, CommandChannel commandChannel)
    {
        PostToUi(() =>
        {
            commandChannel?.Dispose();
            ResetLinkTracking();
            _channel = null;
            ShowPrompt();
        });
    }
}
