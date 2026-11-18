using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

internal class LinkTracker
{
    private ILinkScanner _scanner;

    private TextDocument.TextPosition _expectedPosition;

    public bool IsActive { get; private set; } = true;

    public TextDocument.TextPosition ExpectedPosition
    {
        get
        {
            return _expectedPosition;
        }
        set
        {
            _expectedPosition = value;
        }
    }

    public LinkTracker(ILinkScanner linkScanner, TextDocument.TextPosition textPosition)
    {
        _scanner = linkScanner;
        _expectedPosition = textPosition;
    }

    private static TextDocument.TextPosition NextLineStart(TextDocument.TextPosition textPosition)
    {
        return new TextDocument.TextPosition(0, textPosition.Line + 1);
    }

    /// <param name="wrapped">The text goes on in the next row because the one above it was full (auto-wrap), not after a line break.</param>
    public LinkCell[] Scan(string text, TextDocument.TextPosition textPosition, bool wrapped = false)
    {
        if (!this.IsActive || text == null)
        {
            return null;
        }
        bool nextLine = textPosition == NextLineStart(_expectedPosition);
        if (textPosition != _expectedPosition && !nextLine)
        {
            _scanner?.Finish();
            this.IsActive = false;
            return null;
        }
        _expectedPosition = new TextDocument.TextPosition(textPosition.Column + text.Length, textPosition.Line);
        if (_scanner is IColumnAwareLinkScanner columnAware)
        {
            // A wrapped row is the rest of the same line of output: a long address goes on in it (column -1: "not a new line").
            return columnAware.Scan(text, wrapped && nextLine ? -1 : textPosition.Column);
        }
        return _scanner.Scan(text);
    }

    public void Finish()
    {
        _scanner?.Finish();
    }
}
