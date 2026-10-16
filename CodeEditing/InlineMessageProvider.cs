using CodeEditPP.Properties;
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

        public InlineMessage(MessageSeverity messageSeverity, string text, int value2, int value3)
        {
            _severity = messageSeverity;
            this._text = text;
            this.OriginalLine = (Line = value2);
            this.Column = value3;
        }

        public void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int value2, int value3, int value4, Rectangle rectangle2, SizeF sizeF)
        {
            int num = -1;
            if (this.Column != -1)
            {
                num = readonlyTextBrowser.Document.IndexToColumn(value4, this.Column, readonlyTextBrowser.TabSize) - value3;
                if (num < 0)
                {
                    num = 0;
                }
            }
            StringFormat format = new StringFormat(StringFormat.GenericDefault);
            Font font = new Font(FontFamily.GenericSerif, rectangle2.Height * 5 / 8);
            Image image = ((this._severity == MessageSeverity.Error) ? Resources.error : Resources.warning);
            int height = rectangle2.Height;
            ImageAttributes imageAttributes = new ImageAttributes();
            int num2 = (int)graphics.MeasureString(this._text, font).Width + 3;
            if (num2 > rectangle2.Width - (4 + height + 3) * 2)
            {
                num2 = rectangle2.Width - (4 + height + 3) * 2;
            }
            int num3 = (rectangle2.Width - num2) / 2 + 3;
            if (num != -1)
            {
                num3 = (int)((float)num * sizeF.Width) + height + 4;
            }
            Rectangle destRect = new Rectangle(rectangle2.Left + num3 - height - 4, rectangle2.Top, height, height);
            Rectangle rectangle = new Rectangle(rectangle2.Left + num3, rectangle2.Top, num2 + 1, rectangle2.Height);
            Rectangle rect = new Rectangle(destRect.Left - 3, rectangle2.Top, rectangle.Right - destRect.Left, rectangle.Height);
            graphics.FillRectangle(Brushes.LightGray, rect);
            imageAttributes.SetColorKey(Color.Magenta, Color.Magenta);
            graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, imageAttributes);
            graphics.DrawString(this._text, font, (this._severity == MessageSeverity.Error) ? Brushes.Red : Brushes.DarkGoldenrod, rectangle, format);
            graphics.DrawRectangle(Pens.Black, rect);
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

    public void AddMessage(int value2, MessageSeverity messageSeverity, string text, int value3)
    {
        _messages[value2] = new InlineMessage(messageSeverity, text, value2, value3);
        if (value2 < this._firstLine)
        {
            this._firstLine = value2;
        }
        if (value2 > _lastLine)
        {
            _lastLine = value2;
        }
    }

    public void ShiftLines(int value3, int value4, int value5)
    {
        int num = value5 - value4;
        Dictionary<int, InlineMessage> dictionary = new Dictionary<int, InlineMessage>();
        foreach (KeyValuePair<int, InlineMessage> item in _messages)
        {
            InlineMessage value = item.Value;
            if (value.Line > value3)
            {
                if (value.Line > value3 + value4)
                {
                    value.Line += num;
                }
                else
                {
                    value.Line = -1;
                }
                dictionary[item.Key] = value;
            }
        }
        foreach (int key in dictionary.Keys)
        {
            _messages.Remove(key);
        }
        foreach (InlineMessage value2 in dictionary.Values)
        {
            if (value2.Line != -1)
            {
                _messages[value2.Line] = value2;
            }
        }
    }

    public int GetCurrentLine(int value2)
    {
        foreach (InlineMessage value in _messages.Values)
        {
            if (value.OriginalLine == value2)
            {
                return value.Line;
            }
        }
        return value2;
    }

    public int GetTotalVirtualRows()
    {
        return _messages.Count;
    }

    public int GetTotalHiddenLines()
    {
        return 0;
    }

    public int GetNextSpecialLine(int value2)
    {
        if (value2 <= _firstLine)
        {
            return _firstLine;
        }
        if (value2 > _lastLine)
        {
            return -1;
        }
        int num = int.MaxValue;
        foreach (int key in _messages.Keys)
        {
            if (key >= value2 && key <= num)
            {
                num = key;
            }
        }
        return num;
    }

    public int GetVirtualRowCount(int value2)
    {
        if (_messages.ContainsKey(value2))
        {
            return 1;
        }
        return 0;
    }

    public void GetVirtualLines(int value2, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
        if (_messages.TryGetValue(value2, out var value))
        {
            list.Add(value);
        }
    }

    public int GetHiddenLineCount(int value2)
    {
        return 0;
    }

    public ReadonlyTextBrowser.ILineDecorator GetDecorator(int value2)
    {
        return null;
    }

    public bool OnCaretMoved(int value2, int value3)
    {
        return false;
    }
}
