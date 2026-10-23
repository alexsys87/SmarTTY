using SmarTTY.Controls;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace SmarTTY.CodeEditing;

public class InlineMessageProvider : ReadonlyTextBrowser.IVirtualLineProvider
{
    public class InlineMessage : ReadonlyTextBrowser.IVirtualLine
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

        public void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int virtualLineIndex, int scrollColumn, int lineIndex, Rectangle bounds, SizeF charSize)
        {
            int indentColumn = -1;
            if (this.Column != -1)
            {
                indentColumn = readonlyTextBrowser.Document.IndexToColumn(lineIndex, this.Column, readonlyTextBrowser.TabSize) - scrollColumn;
                if (indentColumn < 0)
                {
                    indentColumn = 0;
                }
            }
            StringFormat format = new StringFormat(StringFormat.GenericDefault);
            Font font = new Font(FontFamily.GenericSerif, bounds.Height * 5 / 8);
            Image image = Icons.Load((this._severity == MessageSeverity.Error) ? "message-error" : "message-warning");
            int height = bounds.Height;
            ImageAttributes imageAttributes = new ImageAttributes();
            int textWidth = (int)graphics.MeasureString(this._text, font).Width + 3;
            if (textWidth > bounds.Width - (4 + height + 3) * 2)
            {
                textWidth = bounds.Width - (4 + height + 3) * 2;
            }
            int xPos = (bounds.Width - textWidth) / 2 + 3;
            if (indentColumn != -1)
            {
                xPos = (int)((float)indentColumn * charSize.Width) + height + 4;
            }
            Rectangle destRect = new Rectangle(bounds.Left + xPos - height - 4, bounds.Top, height, height);
            Rectangle textRect = new Rectangle(bounds.Left + xPos, bounds.Top, textWidth + 1, bounds.Height);
            Rectangle backgroundRect = new Rectangle(destRect.Left - 3, bounds.Top, textRect.Right - destRect.Left, bounds.Height);
            graphics.FillRectangle(Brushes.LightGray, backgroundRect);
            imageAttributes.SetColorKey(Color.Magenta, Color.Magenta);
            graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, imageAttributes);
            graphics.DrawString(this._text, font, (this._severity == MessageSeverity.Error) ? Brushes.Red : Brushes.DarkGoldenrod, textRect, format);
            graphics.DrawRectangle(Pens.Black, backgroundRect);
        }
    }

    public enum MessageSeverity
    {
        Error,
        Warning
    }

    private Dictionary<int, InlineMessage> _messages = new Dictionary<int, InlineMessage>();

    private int _firstLine;

    private int _lastLine;

    public InlineMessageProvider()
    {
        _firstLine = int.MaxValue;
        _lastLine = 0;
    }

    public void Clear()
    {
        _messages.Clear();
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

    public int GetTotalHiddenLines()
    {
        return 0;
    }

    public int GetNextSpecialLine(int lineIndex)
    {
        if (lineIndex <= _firstLine)
        {
            return _firstLine;
        }
        if (lineIndex > _lastLine)
        {
            return -1;
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

    public void GetVirtualLines(int lineIndex, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
        if (_messages.TryGetValue(lineIndex, out var msg))
        {
            list.Add(msg);
        }
    }

    public int GetHiddenLineCount(int lineIndex)
    {
        return 0;
    }

    public ReadonlyTextBrowser.ILineDecorator GetDecorator(int lineIndex)
    {
        return null;
    }

    public bool OnCaretMoved(int lineIndex, int columnIndex)
    {
        return false;
    }
}
