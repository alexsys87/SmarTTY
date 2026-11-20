using SmarTTY.CodeEditing;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace SmarTTY.Controls;

/// <summary>
/// Shows the messages of an <see cref="InlineMessageProvider"/> as rows under the lines of the text view and paints them: the
/// band, the icon, the text. It owns what the painting needs (the font, the icons, the colours of the theme).
/// </summary>
public class InlineMessageView : ReadonlyTextBrowser.IVirtualLineProvider, System.IDisposable
{
    // The row of a message is one object for as long as the message is there.
    private readonly ConditionalWeakTable<InlineMessageProvider.InlineMessage, InlineMessageRow> _rows = new ConditionalWeakTable<InlineMessageProvider.InlineMessage, InlineMessageRow>();

    private readonly InlineMessageProvider _messages;

    private class InlineMessageRow : ReadonlyTextBrowser.IVirtualLine
    {
        private readonly InlineMessageProvider.InlineMessage _message;

        // The view that shows this message: it owns the font and the icons the row is drawn with.
        private readonly InlineMessageView _provider;

        public InlineMessageRow(InlineMessageView provider, InlineMessageProvider.InlineMessage message)
        {
            _provider = provider;
            _message = message;
        }

        public void Draw(Graphics graphics, ReadonlyTextBrowser readonlyTextBrowser, int virtualLineIndex, int scrollColumn, int lineIndex, Rectangle bounds, SizeF charSize)
        {
            int indentColumn = InlineMessageBand.IndentColumn(_message.Column, readonlyTextBrowser.Document.IndexToColumn(lineIndex, _message.Column, readonlyTextBrowser.TabSize), scrollColumn);
            InlineMessageView provider = _provider;
            provider.EnsureDrawingObjects(bounds.Height);
            Font font = provider._font;
            Image image = (_message.Severity == InlineMessageProvider.MessageSeverity.Error) ? provider._errorIcon : provider._warningIcon;
            int height = bounds.Height;
            InlineMessageBand band = InlineMessageBand.Place(bounds.Left, bounds.Width, height, graphics.MeasureString(_message.Text, font).Width, indentColumn, charSize.Width);
            Rectangle destRect = new Rectangle(band.IconLeft, bounds.Top, height, height);
            Rectangle textRect = new Rectangle(band.TextLeft, bounds.Top, band.TextWidth, bounds.Height);
            Rectangle backgroundRect = new Rectangle(band.BandLeft, bounds.Top, band.BandWidth, bounds.Height);
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
            using (SolidBrush textBrush = new SolidBrush((_message.Severity == InlineMessageProvider.MessageSeverity.Error) ? provider.ErrorColor : provider.WarningColor))
            {
                graphics.DrawString(_message.Text, font, textBrush, textRect, provider._format);
            }
            using Pen borderPen = new Pen(provider.BorderColor);
            graphics.DrawRectangle(borderPen, backgroundRect);
        }
    }

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

    public InlineMessageView(InlineMessageProvider messages)
    {
        _messages = messages;
    }

    /// <summary>Releases the font and the format of the rows; the editor does it when it is disposed.</summary>
    public void Dispose()
    {
        _font?.Dispose();
        _font = null;
        _format.Dispose();
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

    public int GetTotalVirtualRows()
    {
        return _messages.GetTotalVirtualRows();
    }

    public int GetTotalHiddenLines()
    {
        return 0;
    }

    public int GetNextSpecialLine(int lineIndex)
    {
        return _messages.GetNextSpecialLine(lineIndex);
    }

    public int GetVirtualRowCount(int lineIndex)
    {
        return _messages.GetVirtualRowCount(lineIndex);
    }

    public void GetVirtualLines(int lineIndex, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
        if (_messages.TryGetMessage(lineIndex, out var msg))
        {
            list.Add(_rows.GetValue(msg, (InlineMessageProvider.InlineMessage message) => new InlineMessageRow(this, message)));
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
