using SmarTTY.TextEditing;

namespace SmarTTY.Terminal.Emulation;

public struct CellStyle
{
    public class ForegroundOverride
    {
        /// <summary>The colour of the text; null when the override has none (the colours of the cell apply).</summary>
        public RgbColor? Color;
    }

    public byte Foreground;

    public byte Background;

    public bool Bold;

    public bool Inverse;

    public bool Underline;

    /// <summary>
    /// 256-colour or 24-bit foreground set by SGR 38/90-97; 0 when <see cref="Foreground"/> applies.
    /// Encoded as palette index + 1 (1..256) or <see cref="XtermProtocol.RgbColorFlag"/> | 0xRRGGBB.
    /// </summary>
    public int ExtendedForeground;

    /// <summary>The same as <see cref="ExtendedForeground"/> for the background (SGR 48/100-107).</summary>
    public int ExtendedBackground;

    public ForegroundOverride Override;

    internal LinkCell _link;

    private static readonly string[] ColorNames = new string[9] { "Default", "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White" };

    public static readonly CellStyle Default = default;

    public bool IsDefault
    {
        get
        {
            if (Foreground == 0 && Background == 0 && ExtendedForeground == 0 && ExtendedBackground == 0 && !Bold && !Inverse && !Underline && Override == null)
            {
                return _link.Target == null && _link.Severity == 0;
            }
            return false;
        }
    }

    public bool StyleEquals(CellStyle cellStyle)
    {
        if (Foreground == cellStyle.Foreground && Background == cellStyle.Background && ExtendedForeground == cellStyle.ExtendedForeground && ExtendedBackground == cellStyle.ExtendedBackground && Bold == cellStyle.Bold && Inverse == cellStyle.Inverse && Underline == cellStyle.Underline && object.Equals(Override, cellStyle.Override))
        {
            return object.Equals(_link, cellStyle._link);
        }
        return false;
    }

    private static string GetColorName(byte colorIndex)
    {
        if (colorIndex >= ColorNames.Length)
        {
            return "Invalid";
        }
        return ColorNames[colorIndex];
    }

    public override string ToString()
    {
        if (IsDefault)
        {
            return "Default";
        }
        string text = $"{GetColorName(Foreground)} on {GetColorName(Background)}";
        if (Inverse)
        {
            text += " [inverse]";
        }
        return text;
    }
}
