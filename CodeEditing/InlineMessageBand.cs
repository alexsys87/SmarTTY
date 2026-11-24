namespace SmarTTY.CodeEditing;

/// <summary>
/// Where the row of an inline message puts its band, its icon and its text (in pixels): under the column of the message (the icon in front of the
/// column, the text after it) or, for a message without a column, in the middle of the row; the text no wider than the row leaves room for. It is
/// the arithmetic of the row of <c>Controls/TextView/InlineMessageView</c>, moved here as it was (stage 3D) so that a text view of another toolkit
/// draws the messages in the same place; the view measures the text and draws.
/// </summary>
public struct InlineMessageBand
{
    /// <summary>The left edge of the icon (a square of the height of the row).</summary>
    public int IconLeft;

    /// <summary>The left edge of the text.</summary>
    public int TextLeft;

    /// <summary>The width the text is drawn in.</summary>
    public int TextWidth;

    /// <summary>The left edge of the band behind the icon and the text (its frame is drawn around it).</summary>
    public int BandLeft;

    /// <summary>The width of the band.</summary>
    public int BandWidth;

    /// <summary>
    /// The column of the row a message goes under: -1 for a message without a column (<paramref name="messageColumn"/> -1: it goes in the middle),
    /// else the display column of the message less the columns scrolled away, not less than 0.
    /// </summary>
    public static int IndentColumn(int messageColumn, int displayColumn, int scrollColumn)
    {
        int indentColumn = -1;
        if (messageColumn != -1)
        {
            indentColumn = displayColumn - scrollColumn;
            if (indentColumn < 0)
            {
                indentColumn = 0;
            }
        }
        return indentColumn;
    }

    /// <summary>
    /// The band of a row from <paramref name="left"/>, <paramref name="width"/> wide and <paramref name="height"/> high, for a text measured
    /// <paramref name="measuredWidth"/> wide, under <paramref name="indentColumn"/> (-1: in the middle) with characters <paramref name="charWidth"/> wide.
    /// </summary>
    public static InlineMessageBand Place(int left, int width, int height, float measuredWidth, int indentColumn, float charWidth)
    {
        int textWidth = (int)measuredWidth + 3;
        if (textWidth > width - (4 + height + 3) * 2)
        {
            textWidth = width - (4 + height + 3) * 2;
        }
        int xPos = (width - textWidth) / 2 + 3;
        if (indentColumn != -1)
        {
            xPos = (int)((float)indentColumn * charWidth) + height + 4;
        }
        InlineMessageBand band;
        band.IconLeft = left + xPos - height - 4;
        band.TextLeft = left + xPos;
        band.TextWidth = textWidth + 1;
        band.BandLeft = band.IconLeft - 3;
        band.BandWidth = band.TextLeft + band.TextWidth - band.IconLeft;
        return band;
    }
}
