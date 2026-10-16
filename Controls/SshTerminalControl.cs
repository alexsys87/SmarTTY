using ManagedLibSSH2;
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
                for (int num = _startLine; num < editableTextDocument.LineCount; num++)
                {
                    string value = editableTextDocument.GetLineText(num);
                    stringBuilder.Append(value);
                }
                return stringBuilder.ToString().Substring(_startColumn);
            }
            set
            {
                EditableTextDocument editableTextDocument2 = ((EditableTextBrowser)_terminal).EditableDocument;
                _terminal.ReplaceText(StartPosition, editableTextDocument2.EndPosition, flag: false, value ?? "");
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

        public InputRegion(SshTerminalControl sshTerminalControl, int value2, int value3)
        {
            _terminal = sshTerminalControl;
            this._startColumn = value3;
            _startLine = value2;
        }

        public int IndexOf(TextDocument.TextPosition textPosition)
        {
            EditableTextDocument editableTextDocument = ((EditableTextBrowser)_terminal).EditableDocument;
            int num = -this._startColumn;
            for (int num2 = _startLine; num2 < editableTextDocument.LineCount; num2++)
            {
                int length = editableTextDocument.GetLineText(num2).Length;
                if (num2 == textPosition.Line)
                {
                    return num + textPosition.Column;
                }
                num += length;
            }
            return -1;
        }

        public TextDocument.TextPosition PositionAt(int value2)
        {
            if (value2 >= 0)
            {
                int num = value2 + this._startColumn;
                int num2 = 0;
                for (int num3 = _startLine; num3 < ((EditableTextBrowser)_terminal).EditableDocument.LineCount; num3++)
                {
                    int length = ((EditableTextBrowser)_terminal).EditableDocument.GetLineText(num3).Length;
                    if (num >= num2 && num <= num2 + length)
                    {
                        return new TextDocument.TextPosition(num - num2, num3);
                    }
                    num2 += length;
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
                int num = Region.IndexOf(_terminal.CaretPosition);
                string text = Text;
                if (text != null && num >= 0 && num <= text.Length)
                {
                    if (num == 0)
                    {
                        return true;
                    }
                    if (text[num - 1] == ' ')
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public InputLine(SshTerminalControl sshTerminalControl, int value2, string text)
        {
            _terminal = sshTerminalControl;
            this.StartLine = value2;
            Region = new InputRegion(sshTerminalControl, value2, text.Length);
            Prompt = text;
        }

        public void SetText(string text)
        {
            Region.Text = text;
            Region.CaretIndex = -1;
        }

        public void NavigateHistory(bool flag)
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
            if (flag)
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
            string[] array = _terminal._shellCompletionAnalyzer?.GetHistory();
            List<string> list = new List<string>();
            HashSet<string> hashSet = new HashSet<string>();
            if (array == null)
            {
                array = new string[0];
            }
            foreach (string item in array.Concat(_terminal._typedCommands).Reverse())
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

        private void OnDataReceived(object obj, byte[] buffer, int value2, int value3, int value4)
        {
            string text = _decoder.Decode(buffer, value2, value3);
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

        public void Resize(int value2, int value3)
        {
            _channel.ResizePty(value2, value3);
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

    protected override bool UsesTerminalInput => _inputLine != null;

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

    public SshTerminalControl(ITerminalSessionContext terminalSessionContext, string text2 = null)
    {
        _context = terminalSessionContext;
        _homeDirectory = (_currentDirectory = terminalSessionContext.Session.RunCommandChecked("pwd", flag: true)?.Where((string text) => text != "")?.LastOrDefault());
        if (text2 != null)
        {
            _currentDirectory = text2;
        }
        _linkExtensions = new HashSet<string>();
        string[] array = (terminalSessionContext.VTSettings.AutoLinkExtensions ?? "").Split(';');
        foreach (string item in array)
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

    public void RequestOpenFile(string text, int? value2, int? value3)
    {
        OpenFileRequested?.Invoke(this, new OpenFileEventArgs
        {
            Path = text,
            Column = value3,
            Line = value2
        });
    }

    private void OnDataEntered(object obj, byte[] buffer, InputKind inputKind)
    {
        _channel?.SendInput(buffer, inputKind);
    }

    private void OnTerminalSizeChanged(TerminalEmulatorControl terminalEmulatorControl, int value2, int value3)
    {
        _channel?.Resize(value2, value3);
    }

    public bool IsLinkExtension(string text)
    {
        return _linkExtensions?.Contains(text) ?? false;
    }

    private void UpdateTitle()
    {
        base.Title = PromptPrefix;
    }

    public void ChangeDirectory(string text3)
    {
        if (text3.StartsWith("~"))
        {
            text3 = _homeDirectory + text3.Substring(1);
        }
        text3 = ((!text3.StartsWith("\"") && !text3.StartsWith("'")) ? text3.Replace("\\ ", " ") : text3.Trim('\'', '"'));
        string text = RemotePathUtils.NormalizeUnix(RemotePathUtils.Combine(_currentDirectory, text3));
        string text2 = text.TrimEnd('/');
        if (text2.Length > 0)
        {
            text = text2;
        }
        List<string> list = _context.Session.RunCommand("cd \"" + text + "\"", out var num) ?? new List<string>();
        foreach (string item in list)
        {
            ProcessOutput(item);
        }
        if (num == 0)
        {
            _currentDirectory = text;
            DirectoryChanged?.Invoke(text);
        }
        else if (list.Count == 0)
        {
            ProcessOutput(TerminalEmulatorControl.Colorize($"'cd {text}' exited with code {num ?? (-1)}", 1));
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

    private ILinkTarget GetLinkAt(int value2, Point point)
    {
        LineInfo lineInfo = ActiveBuffer?.Lines?.GetLine(value2);
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

    protected override HoverTarget GetHoverTarget(int value2, Point point)
    {
        ILinkTarget linkTarget = GetLinkAt(value2, point);
        if (linkTarget != null && linkTarget.IsActive)
        {
            return new HoverTarget
            {
                Cursor = Cursors.Hand,
                ClickAction = linkTarget.Activate
            };
        }
        return base.GetHoverTarget(value2, point);
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
        for (int num = _inputLine.StartLine; num < ((EditableTextBrowser)this).EditableDocument.LineCount; num++)
        {
            int length = ((EditableTextBrowser)this).EditableDocument.GetLineText(num).Length;
            bool flag = num == ((EditableTextBrowser)this).EditableDocument.LineCount - 1;
            if (length > _visibleColumns)
            {
                return true;
            }
            if (!flag && length != _visibleColumns)
            {
                return true;
            }
        }
        return false;
    }

    protected override void OnResize(EventArgs e2)
    {
        int? num = _inputLine?.Region?.CaretIndex;
        base.OnResize(e2);
        if (!RewrapInput(num) && num.HasValue && _inputLine != null)
        {
            _inputLine.Region.CaretIndex = num.Value;
        }
    }

    public override void ReplaceText(TextDocument.TextPosition textPosition, TextDocument.TextPosition textPosition2, bool flag, string text)
    {
        if (IsInInputArea(textPosition) && (flag || IsInInputArea(textPosition2)) && _inputLine != null)
        {
            int num = _inputLine.Region.IndexOf(textPosition);
            int num2 = ((!flag) ? _inputLine.Region.IndexOf(textPosition2) : num);
            if (num2 < num)
            {
                int num3 = num2;
                num2 = num;
                num = num3;
            }
            ReplaceTextCore(textPosition, textPosition2, flag, text);
            RewrapInput();
            string filter = _inputLine?.Text;
            _popup?.SetFilter(filter);
            CompletionSession completionSession = _completion;
            if (completionSession != null && completionSession.OnTextChanged(filter, num, num2 - num, (text ?? "").Replace("\n", "").Replace("\r", "").Length))
            {
                _completion.Dispose();
                _completion = null;
            }
        }
        else
        {
            base.ReplaceText(textPosition, textPosition2, flag, text);
        }
    }

    private bool RewrapInput(int? value2 = null)
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
        int num = value2 ?? _inputLine.Region.CaretIndex;
        StringBuilder stringBuilder = new StringBuilder();
        for (int num2 = 0; num2 < text.Length; num2 += _visibleColumns)
        {
            int length = Math.Min(text.Length - num2, _visibleColumns);
            stringBuilder.Append(text.Substring(num2, length));
            if (num2 + _visibleColumns < text.Length)
            {
                stringBuilder.Append("\n");
            }
        }
        ReplaceTextCore(new TextDocument.TextPosition(0, inputLine.StartLine), new TextDocument.TextPosition(0, ((EditableTextBrowser)this).EditableDocument.LineCount), flag: false, stringBuilder.ToString());
        OnTextReplaced();
        inputLine.Region.CaretIndex = num;
        return true;
    }

    protected override void PlaceCaretAt(Point point)
    {
        if (_inputLine != null)
        {
            TextDocument.TextPosition textPosition = HitTest(point, flag2: true);
            if (textPosition.IsValid && IsInInputArea(textPosition))
            {
                SetCaret(textPosition);
            }
        }
        base.PlaceCaretAt(point);
    }

    private int FindWordBoundary(int value2, string text, bool flag)
    {
        if (flag)
        {
            do
            {
                value2--;
            }
            while (value2 > 0 && IsWordChar(text[value2 - 1]) && IsWordChar(text[value2]));
            if (value2 < 0)
            {
                value2 = 0;
            }
        }
        else
        {
            do
            {
                value2++;
            }
            while (value2 > 0 && value2 < text.Length && IsWordChar(text[value2 - 1]) && IsWordChar(text[value2]));
            if (value2 > text.Length)
            {
                value2 = text.Length;
            }
        }
        return value2;
    }

    protected override TextDocument.TextPosition ComputeMovedPosition(CaretMove caretMove, Keys keys, TextDocument.TextPosition textPosition, bool flag = false)
    {
        if (caretMove == CaretMove.Home || caretMove == CaretMove.End)
        {
            keys |= Keys.Control;
        }
        TextDocument.TextPosition result = base.ComputeMovedPosition(caretMove, keys, textPosition, flag);
        if ((caretMove == CaretMove.WordLeft || caretMove == CaretMove.WordRight) && _inputLine != null)
        {
            int num = _inputLine.Region.IndexOf(textPosition);
            string text = _inputLine?.Text;
            if (text != null && num >= 0 && num < text.Length)
            {
                int num2 = FindWordBoundary(num, text, caretMove == CaretMove.WordLeft);
                result = _inputLine.Region.PositionAt(num2);
            }
        }
        InputLine inputLine = _inputLine;
        if (inputLine != null && result < inputLine.StartPosition)
        {
            result = inputLine.StartPosition;
        }
        return result;
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
            SetSelection(flag: true, new TextDocument.TextPosition(_inputLine.Prompt.Length, _inputLine.StartLine));
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
        int value2 = inputLine.Region.CaretIndex;
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
        int? num = Enumerable.Range(0, items.Length).Select((Func<int, int?>)((int value) => value)).FirstOrDefault((int? num3) => items[num3.Value].Contains(value2));
        int? num2 = null;
        if (num.HasValue)
        {
            num2 = num.Value - 1;
        }
        else if (items.Length != 0)
        {
            num2 = items.Length - 1;
        }
        CompletionSession.Token token = default;
        if (num.HasValue)
        {
            token = items[num.Value];
        }
        CompletionTarget completionTarget;
        if (items.Length == 0 || num == 0 || (num2.HasValue && items[num2.Value].Text == "|"))
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

    private void OnCompletionApplied(object sender, CompletionSession.AppliedEventArgs e2)
    {
        InputRegion inputRegion = _inputLine?.Region;
        if (inputRegion != null)
        {
            inputRegion.Text = e2.NewText;
            inputRegion.CaretIndex = e2.CaretIndex;
        }
        if (e2.Commit)
        {
            SubmitInput();
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        bool? flag = null;
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
                    if (e.KeyChar > ' ' && _context.VTSettings.CompletionTriggers.FirstCharacterInWord)
                    {
                        flag = _inputLine?.IsAtWordStart;
                    }
                    break;
            }
            CompletionSession completionSession2 = _completion;
            if (completionSession2 != null && completionSession2.IsOpen && completionSession2.HandleChar(e.KeyChar))
            {
                _completion = null;
                e.Handled = true;
                switch (e.KeyChar)
                {
                    case ' ':
                        if (_context.VTSettings.CompletionTriggers.Space)
                        {
                            ShowCompletions(CompletionTrigger.Auto);
                        }
                        break;
                    case '/':
                        if (_context.VTSettings.CompletionTriggers.Slash)
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
                if (_context.VTSettings.CompletionTriggers.Space)
                {
                    ShowCompletions(CompletionTrigger.Auto);
                }
                break;
            case '/':
                if (_context.VTSettings.CompletionTriggers.Slash)
                {
                    ShowCompletions();
                }
                break;
            default:
                if (_context.VTSettings.CompletionTriggers.FirstCharacterInWord && flag == true)
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

    protected override void OnKeyDown(KeyEventArgs e2)
    {
        CompletionSession completionSession = _completion;
        ICompletionPopup completionPopup = _popup;
        if ((completionPopup != null && completionPopup.TryHandleKeyEvent(e2)) || (completionSession != null && completionSession.HandleKey(e2)))
        {
            e2.Handled = true;
            e2.SuppressKeyPress = true;
            CompletionSession.AppliedEventArgs appliedEventArgs = completionSession?.LastApplied;
            if (appliedEventArgs != null && appliedEventArgs.IsDirectory && e2.KeyCode != Keys.Return)
            {
                _completion = null;
                ShowCompletions();
            }
            return;
        }
        if (_inputLine != null)
        {
            if (e2.KeyCode == Keys.Escape)
            {
                CloseCompletion();
            }
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                switch (e2.KeyCode)
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
                                Items = _inputLine.BuildHistory()?.Select((string text2) => new CompletionItem
                                {
                                    Icon = VirtualTerminalPopupItemIcon.Command,
                                    Text = text2
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
                        if (_context.VTSettings.CompletionTriggers.CtrlSpace)
                        {
                            ShowCompletions();
                            e2.Handled = true;
                            e2.SuppressKeyPress = true;
                            return;
                        }
                        break;
                }
            }
            else
            {
                Keys keyCode = e2.KeyCode;
                if (keyCode == Keys.Up || keyCode == Keys.Down)
                {
                    _inputLine.NavigateHistory(e2.KeyCode == Keys.Up);
                    e2.Handled = true;
                    e2.SuppressKeyPress = true;
                }
            }
        }
        base.OnKeyDown(e2);
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

    public string BuildRemoteCommand(string text2, bool? flag = null)
    {
        string text = "cd \"" + _currentDirectory + "\" && ";
        flag = flag ?? _context.VTSettings.InteractiveModeWrapper;
        foreach (KeyValuePair<string, string> item in _environment)
        {
            text2 = item.Key + "=" + item.Value + " " + text2;
        }
        if (flag.Value)
        {
            text2 = _context.VTSettings.DefaultShell + " -i -c \"" + ShellCompletionAnalyzer.EscapeForShell(text2) + "\"";
        }
        return text + text2;
    }

    public bool SubmitInput()
    {
        return Submit(null);
    }

    public bool Submit(string text3)
    {
        if (_inputLine == null)
        {
            return false;
        }
        if (text3 != null)
        {
            _inputLine.SetText(text3);
        }
        else
        {
            text3 = _inputLine?.Text;
        }
        if (string.IsNullOrEmpty(text3))
        {
            ShowPrompt();
            return true;
        }
        BeginExecution();
        try
        {
            _typedCommands.Add(text3);
            _shellCompletionAnalyzer.AppendToHistory(text3);
            if (text3.StartsWith("cd "))
            {
                string text = text3.Substring(2).Trim();
                ChangeDirectory(text);
            }
            else if (text3.StartsWith("export "))
            {
                string text2 = text3.Substring(6).Trim();
                int num = text2.IndexOf('=');
                if (num != -1)
                {
                    string key = text2.Substring(0, num);
                    if (num == text2.Length - 1)
                    {
                        _environment.Remove(key);
                    }
                    else
                    {
                        _environment[key] = text2.Substring(num + 1);
                    }
                }
                ShowPrompt();
            }
            else
            {
                SshShellChannel sshShellChannel = _context.Session.OpenShellChannel(BuildRemoteCommand(text3), "xterm", ExtendedDataMode.Merge);
                if (sshShellChannel == null)
                {
                    throw new Exception("Failed to open an SSH channel");
                }
                CommandRun item = new CommandRun(this, text3, _currentDirectory, ((EditableTextBrowser)this).EditableDocument.LineCount - 1);
                _runs.Add(item);
                CommandChannel commandChannel = new CommandChannel(sshShellChannel, this, item);
                _channel = commandChannel;
                sshShellChannel.Finished += (object obj) =>
                {
                    OnCommandFinished(text3, commandChannel);
                };
                sshShellChannel.PollingEnabled = true;
                sshShellChannel.ResizePty(TerminalSize.Width, TerminalSize.Height);
            }
        }
        catch (Exception ex)
        {
            ProcessOutput(TerminalEmulatorControl.Colorize(ex.Message, 1));
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
        bool flag;
        lock (_uiQueue)
        {
            flag = _uiQueue.Count == 0;
            _uiQueue.Enqueue(action);
        }
        if (flag)
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
