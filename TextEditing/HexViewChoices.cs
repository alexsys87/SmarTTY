namespace SmarTTY.TextEditing;

/// <summary>The layouts the hex view offers (moved from <c>HexEditorControl</c> as they were, so that a menu that is not the control's can list them).</summary>
public static class HexViewChoices
{
    /// <summary>The row widths the view offers.</summary>
    public static readonly int[] BytesPerRow = { 8, 16, 32 };

    /// <summary>The group sizes the view offers: single bytes, 16-, 32- and 64-bit words.</summary>
    public static readonly int[] GroupSize = { 1, 2, 4, 8 };
}
