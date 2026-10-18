using SmarTTY.Forms;
using SmarTTY.Native;
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
            _caretIndex = Math.Clamp(index, 0, _text.Length);
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
                .Where(cmd => cmd.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
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

    public SerialTerminalControl()
    {
        // Enable key mapping for proper escape sequences
        _keyMappingEnabled = true;
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
        if (_commandHistory.Count > 0 && _commandHistory[^1] == trimmed)
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

        // Handle Escape to cancel history search
        if (e.KeyCode == Keys.Escape && _historySearch != null)
        {
            CancelHistorySearch();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
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
            if (textPosition.IsValid && textPosition >= _inputLine.StartPosition)
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