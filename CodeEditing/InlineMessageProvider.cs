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

        // The provider that holds this message: it owns the font and the icons the row is drawn with.
        private readonly InlineMessageProvider _provider;

        public InlineMessage(InlineMessageProvider provider, MessageSeverity messageSeverity, string text, int line, int column)
        {
            _provider = provider;
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
            InlineMessageProvider provider = _provider;
            provider.EnsureDrawingObjects(bounds.Height);
            Font font = provider._font;
            Image image = (this._severity == MessageSeverity.Error) ? provider._errorIcon : provider._warningIcon;
            int height = bounds.Height;
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
            using (SolidBrush backgroundBrush = new SolidBrush(provider.BackColor))
            {
                graphics.FillRectangle(backgroundBrush, backgroundRect);
            }
            if (image != null)
            {
                using ImageAttributes imageAttributes = new ImageAttributes();
                imageAttributes.SetColorKey(Color.Magenta, Color.Magenta);
                graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, imageAttributes);
            }
            using (SolidBrush textBrush = new SolidBrush((this._severity == MessageSeverity.Error) ? provider.ErrorColor : provider.WarningColor))
            {
                graphics.DrawString(this._text, font, textBrush, textRect, provider._format);
            }
            using Pen borderPen = new Pen(provider.BorderColor);
            graphics.DrawRectangle(borderPen, backgroundRect);
        }
    }

    public enum MessageSeverity
    {
        Error,
        Warning
    }

    /// <summary>
    /// One message to add in a batch (see <see cref="CodeEditorPP.AddMessages"/>): the line is counted from 1, the
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

    // The text of a message is drawn with one font per row height and one format for all rows; both are kept here
    // instead of being built on every paint. The images are shared by Icons.Load and are not disposed.
    private Font _font;

    private int _fontHeight;

    private readonly StringFormat _format = new StringFormat(StringFormat.GenericDefault);

    private Image _errorIcon;

    private Image _warningIcon;

    private bool _iconsLoaded;

    /// <summary>The colour of the band a message is drawn on; the theme sets it (see CodeEditorPP.ApplyMessageColors).</summary>
    public Color BackColor { get; set; } = Color.LightGray;

    /// <summary>The colour of an error message and of the border of the band.</summary>
    public Color ErrorColor { get; set; } = Color.Red;

    /// <summary>The colour of a warning message.</summary>
    public Color WarningColor { get; set; } = Color.DarkGoldenrod;

    /// <summary>The colour of the frame around the band.</summary>
    public Color BorderColor { get; set; } = Color.Black;

    public InlineMessageProvider()
    {
        _firstLine = int.MaxValue;
        _lastLine = 0;
    }

    // The font follows the row height; the icons are loaded once, when a message is first drawn.
    private void EnsureDrawingObjects(int rowHeight)
    {
        if (_font == null || _fontHeight != rowHeight)
        {
            _font?.Dispose();
            _font = new Font(FontFamily.GenericSerif, rowHeight * 5 / 8);
            _fontHeight = rowHeight;
        }
        if (!_iconsLoaded)
        {
            _errorIcon = Icons.Load("message-error");
            _warningIcon = Icons.Load("message-warning");
            _iconsLoaded = true;
        }
    }

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
        _messages[line] = new InlineMessage(this, messageSeverity, text, line, column);
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
