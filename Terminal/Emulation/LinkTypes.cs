namespace SmarTTY.Terminal.Emulation;

internal interface ILinkTarget
{
    bool IsActive { get; }

    void Activate();
}

internal struct LinkCell
{
    public ILinkTarget Target;

    /// <summary>
    /// 0: none; 1: the text is a note, 2: a warning, 3: an error (<see cref="SmarTTY.Terminal.DiagnosticHighlighter"/>). The
    /// cell is drawn in the colour of that kind unless the program that wrote it chose a colour of its own.
    /// </summary>
    public byte Severity;
}

internal interface ILinkScanner
{
    LinkCell[] Scan(string text);

    void Finish();
}

/// <summary>A scanner that is also told where on the screen each piece of text goes (column 0 is the start of a line).</summary>
internal interface IColumnAwareLinkScanner : ILinkScanner
{
    LinkCell[] Scan(string text, int column);
}
