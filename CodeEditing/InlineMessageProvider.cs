using System.Collections.Generic;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The messages shown under the lines of the editor (the problems a check found): which line has one, what it says, and where the
/// lines go when the text changes. The rows, the painting, the font, the icons and the colours are in
/// <c>Controls/TextView/InlineMessageView.cs</c>.
/// </summary>
public class InlineMessageProvider : ILineMessages
{
    public class InlineMessage
    {
        private MessageSeverity _severity;

        private string _text;

        public int Line;

        public int Column;

        public readonly int OriginalLine;

        public InlineMessage(MessageSeverity messageSeverity, string text, int line, int column)
        {
            _severity = messageSeverity;
            this._text = text;
            this.OriginalLine = (Line = line);
            this.Column = column;
        }

        /// <summary>An error or a warning.</summary>
        public MessageSeverity Severity => _severity;

        /// <summary>What the message says.</summary>
        public string Text => _text;
    }

    public enum MessageSeverity
    {
        Error,
        Warning
    }

    /// <summary>
    /// One message to add in a batch (see <c>CodeEditorPP.AddMessages</c>): the line is counted from 1, the
    /// column from 1, and a column of 0 means the column is not known (the message is centred in its row).
    /// </summary>
    public struct MessageRequest
    {
        public int Line;

        public MessageSeverity Severity;

        public string Text;

        public int Column;
    }

    private Dictionary<int, InlineMessage> _messages = new Dictionary<int, InlineMessage>();

    private int _firstLine;

    private int _lastLine;

    public InlineMessageProvider()
    {
        _firstLine = int.MaxValue;
        _lastLine = 0;
    }

    /// <summary>How many messages there are.</summary>
    public int Count => _messages.Count;

    public void Clear()
    {
        _messages.Clear();
        RecomputeBounds();
    }

    // The first and the last line that hold a message: GetNextSpecialLine answers from them.
    private void RecomputeBounds()
    {
        _firstLine = int.MaxValue;
        _lastLine = 0;
        foreach (int line in _messages.Keys)
        {
            _firstLine = System.Math.Min(_firstLine, line);
            _lastLine = System.Math.Max(_lastLine, line);
        }
    }

    public void AddMessage(int line, MessageSeverity messageSeverity, string text, int column)
    {
        _messages[line] = new InlineMessage(messageSeverity, text, line, column);
        if (line < this._firstLine)
        {
            this._firstLine = line;
        }
        if (line > _lastLine)
        {
            _lastLine = line;
        }
    }

    /// <summary>
    /// Adds a message as a check reports it: the line counted from 1 (a line past the text goes on the last of
    /// <paramref name="lineCount"/> lines, where there is a row to show it, and one below 1 on the first), the column counted from 1 (0 when it
    /// is not known: the message goes in the middle of its row). It is what <c>Controls/CodeEditorPP.AddMessage</c>, <c>AddMessages</c> and
    /// <c>MessageLine</c> did before they called <see cref="AddMessage"/>, moved here as it was (stage 3D).
    /// </summary>
    public void AddReported(int line, MessageSeverity messageSeverity, string text, int column, int lineCount)
    {
        AddMessage(System.Math.Max(0, System.Math.Min(line - 1, lineCount - 1)), messageSeverity, text, column - 1);
    }

    public void ShiftLines(int startLineIndex, int oldLineCount, int newLineCount)
    {
        int diff = newLineCount - oldLineCount;
        Dictionary<int, InlineMessage> relocated = new Dictionary<int, InlineMessage>();
        foreach (KeyValuePair<int, InlineMessage> item in _messages)
        {
            InlineMessage msg = item.Value;
            if (msg.Line > startLineIndex)
            {
                if (msg.Line > startLineIndex + oldLineCount)
                {
                    msg.Line += diff;
                }
                else
                {
                    msg.Line = -1;
                }
                relocated[item.Key] = msg;
            }
        }
        foreach (int key in relocated.Keys)
        {
            _messages.Remove(key);
        }
        foreach (InlineMessage msg in relocated.Values)
        {
            if (msg.Line != -1)
            {
                _messages[msg.Line] = msg;
            }
        }
        // Messages moved: the bounds are of the old lines (a message pushed below the old last line was never drawn).
        RecomputeBounds();
    }

    public int GetCurrentLine(int lineIndex)
    {
        foreach (InlineMessage msg in _messages.Values)
        {
            if (msg.OriginalLine == lineIndex)
            {
                return msg.Line;
            }
        }
        return lineIndex;
    }

    public int GetTotalVirtualRows()
    {
        return _messages.Count;
    }

    public int GetNextSpecialLine(int lineIndex)
    {
        if (lineIndex <= _firstLine)
        {
            return _firstLine;
        }
        if (lineIndex > _lastLine)
        {
            // No more messages. int.MaxValue, as the fold manager answers: another provider with a line ahead is the nearest.
            return int.MaxValue;
        }
        int minLine = int.MaxValue;
        foreach (int key in _messages.Keys)
        {
            if (key >= lineIndex && key <= minLine)
            {
                minLine = key;
            }
        }
        return minLine;
    }

    public int GetVirtualRowCount(int lineIndex)
    {
        if (_messages.ContainsKey(lineIndex))
        {
            return 1;
        }
        return 0;
    }

    /// <summary>The line has a message under it (the WPF text view asks it through <see cref="ILineMessages"/>).</summary>
    public bool HasMessage(int line)
    {
        return _messages.ContainsKey(line);
    }

    /// <summary>The message under the line, if there is one.</summary>
    public bool TryGetMessage(int lineIndex, out InlineMessage message)
    {
        return _messages.TryGetValue(lineIndex, out message);
    }
}
