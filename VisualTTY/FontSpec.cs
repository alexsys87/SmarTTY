namespace VisualTTY;

/// <summary>
/// The font of the settings as the windows take it (stage 5C; it was a <c>System.Drawing.Font</c>): the family, the size in points, bold
/// and italic. It is made from the fields of <see cref="GlobalSettings"/> and is not written to the settings itself.
/// </summary>
public sealed class FontSpec
{
    public FontSpec(string name, float size, bool bold, bool italic)
    {
        Name = name;
        Size = size;
        Bold = bold;
        Italic = italic;
    }

    /// <summary>The family name.</summary>
    public string Name { get; }

    /// <summary>The size in points (1/72 inch).</summary>
    public float Size { get; }

    public bool Bold { get; }

    public bool Italic { get; }
}
