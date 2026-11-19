namespace SmarTTY.TextEditing;

/// <summary>A colour the hex editor offers for marking bytes, with the name shown in the menus.</summary>
public sealed class HexHighlightColor
{
    public HexHighlightColor(string name, RgbColor color)
    {
        Name = name;
        Color = color;
    }

    public string Name { get; }

    public RgbColor Color { get; }
}

/// <summary>
/// The layouts and the marking colours the hex view offers (moved from <c>HexEditorControl</c> as they were, so that a menu that is not the control's
/// can list them; the colours were GDI+ colours there).
/// </summary>
public static class HexViewChoices
{
    /// <summary>The colours offered for marking bytes.</summary>
    public static readonly HexHighlightColor[] HighlightColors =
    {
        new HexHighlightColor("Yellow", RgbColor.FromArgb(255, 214, 0)),
        new HexHighlightColor("Green", RgbColor.FromArgb(60, 200, 90)),
        new HexHighlightColor("Blue", RgbColor.FromArgb(50, 150, 255)),
        new HexHighlightColor("Red", RgbColor.FromArgb(255, 80, 80)),
        new HexHighlightColor("Orange", RgbColor.FromArgb(255, 150, 30)),
        new HexHighlightColor("Purple", RgbColor.FromArgb(170, 110, 255))
    };

    /// <summary>The row widths the view offers.</summary>
    public static readonly int[] BytesPerRow = { 8, 16, 32 };

    /// <summary>The group sizes the view offers: single bytes, 16-, 32- and 64-bit words.</summary>
    public static readonly int[] GroupSize = { 1, 2, 4, 8 };
}
