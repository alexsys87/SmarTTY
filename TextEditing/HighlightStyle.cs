namespace SmarTTY.TextEditing;

/// <summary>
/// How a highlighted range of text looks: the fill, the border and the text colour. It only describes the style; the view
/// makes (and caches) the brushes and pens from it when it paints, see <c>Controls/TextView/HighlightBrushes.cs</c>.
/// <see cref="RgbColor.Empty"/> means "not drawn", like the missing brush or pen of the former form of this class.
/// </summary>
public class HighlightStyle
{
    /// <summary>
    /// The colour of the fill. When it is <see cref="RgbColor.Empty"/> and <see cref="UseSystemSelectionColors"/> is set,
    /// the fill has the system colour of a selection.
    /// </summary>
    public RgbColor FillColor;

    /// <summary>The colour of the 1-pixel border; <see cref="RgbColor.Empty"/> draws no border.</summary>
    public RgbColor BorderColor;

    /// <summary>
    /// The colour of the highlighted text. When it is <see cref="RgbColor.Empty"/> and <see cref="UseSystemSelectionColors"/>
    /// is set, the text has the system colour of selected text; otherwise the text keeps its own colour.
    /// </summary>
    public RgbColor TextColor;

    /// <summary>
    /// True when an empty fill colour or text colour stands for the system colour of a selection. The view reads the system
    /// colours when it paints, so a selection follows a change of them.
    /// </summary>
    public bool UseSystemSelectionColors;

    /// <summary>Builds a style from the colours; an empty colour is not drawn.</summary>
    public HighlightStyle(RgbColor fillColor, RgbColor borderColor, RgbColor textColor)
    {
        FillColor = fillColor;
        BorderColor = borderColor;
        TextColor = textColor;
    }

    /// <summary>Builds the style of a selection: the system colours of a selection, no border.</summary>
    public HighlightStyle()
    {
        UseSystemSelectionColors = true;
    }
}
