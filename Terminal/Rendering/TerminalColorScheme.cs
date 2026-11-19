using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Rendering;

/// <summary>The colours a cell is painted with: null stands for the default of the view (no colour of its own).</summary>
public struct ResolvedCellColors
{
    /// <summary>The colour of the text; null: the default text colour of the view.</summary>
    public RgbColor? Foreground;

    /// <summary>The colour behind the text; null: the background of the view (nothing is painted).</summary>
    public RgbColor? Background;

    /// <summary>The text is underlined (SGR 4, or a link that is active under the mouse).</summary>
    public bool Underline;

    public bool Equals(ResolvedCellColors other)
    {
        return Nullable.Equals(Foreground, other.Foreground) && Nullable.Equals(Background, other.Background) && Underline == other.Underline;
    }
}

/// <summary>
/// The colours of a terminal and the rule that turns the style of a cell into the colours it is painted with. It is the rule that
/// <c>TerminalEmulatorBase.CreateColorSpan</c> applied with brushes (the WinForms view uses it too, so both views paint the same colours):
/// the 16 colours of the palette, bold that brightens a palette colour, the 256 and 24-bit colours, inverse, the colour that a cell overrides
/// (the echo of a HEX terminal) and the colour of a diagnostic that the program wrote without a colour of its own.
/// </summary>
public sealed class TerminalColorScheme
{
    private readonly RgbColor[] _normal = new RgbColor[8];

    private readonly RgbColor[] _bright = new RgbColor[8];

    /// <summary>The scheme with the PuTTY palette and the default colours of the terminal controls.</summary>
    public TerminalColorScheme()
    {
        uint[] normal = { 0xFF000000, 0xFFBB0000, 0xFF00BB00, 0xFFBBBB00, 0xFF0000BB, 0xFFBB00BB, 0xFF00BBBB, 0xFFBBBBBB };
        uint[] bright = { 0xFF555555, 0xFFFF5555, 0xFF55FF55, 0xFFFFFF55, 0xFF5555FF, 0xFFFF55FF, 0xFF55FFFF, 0xFFFFFFFF };
        for (int index = 0; index < 8; index++)
        {
            _normal[index] = new RgbColor(normal[index]);
            _bright[index] = new RgbColor(bright[index]);
        }
    }

    /// <summary>The colour of text without a colour of its own (the text colour of the view; LightGray by default).</summary>
    public RgbColor DefaultForeground { get; set; } = new RgbColor(0xFFD3D3D3);

    /// <summary>The colour of bold text without a colour of its own (<c>NormalTextColor</c> of the WinForms view; White by default).</summary>
    public RgbColor BoldDefaultForeground { get; set; } = new RgbColor(0xFFFFFFFF);

    /// <summary>The background of the view.</summary>
    public RgbColor DefaultBackground { get; set; } = new RgbColor(0xFF000000);

    /// <summary>The colour of a dimmed line (the output of an earlier session).</summary>
    public RgbColor Inactive { get; set; } = new RgbColor(0xFFA9A9A9);

    /// <summary>Sets one of the 16 colours of the palette (index 0..7, normal or bright).</summary>
    public void SetPaletteColor(int index, bool bright, RgbColor color)
    {
        (bright ? _bright : _normal)[index] = color;
    }

    /// <summary>A colour of the palette: 1..8 as in <see cref="CellStyle.Foreground"/>; null for 0 (the default) or a number past the palette.</summary>
    public RgbColor? GetPaletteColor(byte colorIndex, bool bright)
    {
        RgbColor[] colors = bright ? _bright : _normal;
        if (colorIndex == 0 || colorIndex > colors.Length)
        {
            return null;
        }
        return colors[colorIndex - 1];
    }

    /// <summary>A 256-colour or 24-bit colour (see <see cref="CellStyle.ExtendedForeground"/>); the 16 base colours keep following the palette.</summary>
    public RgbColor? GetExtendedColor(int encodedColor)
    {
        int rgb;
        if ((encodedColor & XtermProtocol.RgbColorFlag) != 0)
        {
            rgb = encodedColor & 0xFFFFFF;
        }
        else
        {
            int index = encodedColor - 1;
            if (index < 8)
            {
                return GetPaletteColor((byte)(index + 1), bright: false);
            }
            if (index < 16)
            {
                return GetPaletteColor((byte)(index - 7), bright: true);
            }
            rgb = XtermProtocol.Xterm256ToRgb(index);
        }
        return RgbColor.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    // The palette entries of the colours of a diagnostic: 2 is red, 4 yellow, 7 cyan (the SGR numbers 31, 33 and 36).
    private static byte SeverityColor(byte severity)
    {
        return severity >= 3 ? (byte)2 : (severity == 2 ? (byte)4 : (byte)7);
    }

    /// <summary>The colours of a cell of a line that is not dimmed.</summary>
    public ResolvedCellColors Resolve(CellStyle cellStyle)
    {
        RgbColor? foreground = (cellStyle.ExtendedForeground != 0)
            ? GetExtendedColor(cellStyle.ExtendedForeground)
            : ((cellStyle.Foreground != 0 || !cellStyle.Bold) ? GetPaletteColor(cellStyle.Foreground, cellStyle.Bold) : BoldDefaultForeground);
        RgbColor? background = (cellStyle.ExtendedBackground != 0) ? GetExtendedColor(cellStyle.ExtendedBackground) : GetPaletteColor(cellStyle.Background, bright: false);
        if (cellStyle.Inverse)
        {
            RgbColor? swap = foreground;
            foreground = background;
            background = swap;
            if (foreground == null)
            {
                foreground = DefaultBackground;
            }
            if (background == null)
            {
                background = DefaultForeground;
            }
        }
        if (cellStyle.Override != null && cellStyle.Override.Color.HasValue)
        {
            foreground = cellStyle.Override.Color.Value;
        }
        else if (cellStyle._link.Severity != 0 && cellStyle.Foreground == 0 && cellStyle.ExtendedForeground == 0 && !cellStyle.Inverse)
        {
            // An error, a warning or a note that the program wrote without a colour of its own.
            foreground = GetPaletteColor(SeverityColor(cellStyle._link.Severity), bright: true);
        }
        return new ResolvedCellColors
        {
            Foreground = foreground,
            Background = background,
            Underline = cellStyle.Underline || (cellStyle._link.Target?.IsActive ?? false)
        };
    }
}
