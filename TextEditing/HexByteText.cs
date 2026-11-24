using System;
using System.Globalization;
using System.Text;

namespace SmarTTY.TextEditing;

/// <summary>
/// What the hex editor says about a byte and how it colours it: the text of the tool tip of a byte (its offset, its value and character, the word
/// of its group in the byte order, the coloured block it is in, a change not saved yet), the colour of a byte by its kind on a dark or light
/// background, and the blend of two colours that the view uses for its dim texts and lines. It is what <c>HexEditorControl</c> had in
/// <c>DescribeByte</c>, <c>ByteTextColor</c> and <c>Blend</c>, moved here as it was, with <see cref="RgbColor"/> for the colours.
/// </summary>
public static class HexByteText
{
    /// <summary>The tool tip of the byte at <paramref name="offset"/>, lines separated by "\n".</summary>
    public static string Describe(HexDocument document, int offset, int groupSize, bool bigEndian)
    {
        StringBuilder text = new StringBuilder();
        byte value = document[offset];
        text.Append("Offset 0x").Append(offset.ToString("X8", CultureInfo.InvariantCulture))
            .Append(" (").Append(offset.ToString(CultureInfo.InvariantCulture)).Append(')');
        text.Append("\nByte 0x").Append(value.ToString("X2", CultureInfo.InvariantCulture))
            .Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (HexDocument.IsPrintable(value))
        {
            text.Append(" = '").Append((char)value).Append('\'');
        }
        if (groupSize > 1)
        {
            int start = offset - offset % groupSize;
            if (start + groupSize <= document.Length)
            {
                ulong word = HexValueFormats.ReadUnsigned(document.GetBytes(start, groupSize), 0, groupSize, bigEndian);
                text.Append("\n").Append((groupSize * 8).ToString(CultureInfo.InvariantCulture)).Append("-bit word, ")
                    .Append(bigEndian ? "big" : "little").Append(" endian: 0x")
                    .Append(word.ToString("X" + (groupSize * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))
                    .Append(" = ").Append(word.ToString(CultureInfo.InvariantCulture));
            }
        }
        HexHighlight highlight = document.GetHighlightAt(offset);
        if (highlight != null)
        {
            text.Append("\n").Append(highlight.Label.Length > 0 ? highlight.Label : "Marked").Append(": 0x")
                .Append(highlight.Start.ToString("X8", CultureInfo.InvariantCulture)).Append(" - 0x")
                .Append((highlight.End - 1).ToString("X8", CultureInfo.InvariantCulture)).Append(", ").Append(HexEditingModel.FormatByteCount(highlight.Length));
        }
        if (document.IsByteChanged(offset))
        {
            text.Append("\nChanged, not saved yet");
        }
        return text.ToString();
    }

    /// <summary>
    /// The colour of the text of a byte: <paramref name="foreground"/> for a printable byte or when the bytes are not coloured by kind; else zero is
    /// dim, 0xFF red, a control character green, the rest (above 0x7F) blue, each in a shade for the brightness of <paramref name="background"/>.
    /// </summary>
    public static RgbColor TextColor(byte value, bool colorByValue, RgbColor background, RgbColor foreground)
    {
        if (!colorByValue || HexDocument.IsPrintable(value))
        {
            return foreground;
        }
        bool dark = IsDark(background);
        if (value == 0x00)
        {
            return Blend(background, foreground, 0.4f);
        }
        if (value == 0xFF)
        {
            return dark ? RgbColor.FromArgb(255, 120, 120) : RgbColor.FromArgb(190, 30, 30);
        }
        if (value < 0x20 || value == 0x7F)
        {
            return dark ? RgbColor.FromArgb(110, 210, 140) : RgbColor.FromArgb(0, 125, 60);
        }
        return dark ? RgbColor.FromArgb(125, 175, 255) : RgbColor.FromArgb(30, 80, 200);
    }

    /// <summary>The colour is dark: its brightness (the lightness of HSL, as <c>Color.GetBrightness</c> of .NET) is below one half.</summary>
    public static bool IsDark(RgbColor color)
    {
        int min = Math.Min(Math.Min(color.R, color.G), color.B);
        int max = Math.Max(Math.Max(color.R, color.G), color.B);
        return (max + min) / (byte.MaxValue * 2f) < 0.5f;
    }

    /// <summary>A colour between <paramref name="background"/> and <paramref name="foreground"/> (opaque).</summary>
    public static RgbColor Blend(RgbColor background, RgbColor foreground, float foregroundShare)
    {
        float share = Math.Max(0f, Math.Min(1f, foregroundShare));
        return RgbColor.FromArgb(
            (int)Math.Round(background.R * (1f - share) + foreground.R * share),
            (int)Math.Round(background.G * (1f - share) + foreground.G * share),
            (int)Math.Round(background.B * (1f - share) + foreground.B * share));
    }
}
