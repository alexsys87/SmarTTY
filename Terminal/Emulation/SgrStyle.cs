using System.Collections.Generic;

namespace SmarTTY.Terminal.Emulation;

/// <summary>The effect of the SGR attributes of an escape sequence (CSI ... m) on the style of the text that is written next.</summary>
public static class SgrStyle
{
    /// <summary>Applies the attributes, in order, to the style.</summary>
    public static void Apply(ref CellStyle style, List<XtermProtocol.SgrAttribute> attributes)
    {
        foreach (XtermProtocol.SgrAttribute attribute in attributes)
        {
            int code = attribute.Code;
            if (code >= 30 && code <= 37)
            {
                style.Foreground = (byte)(1 + code - 30);
                style.ExtendedForeground = 0;
            }
            else if (code >= 40 && code <= 47)
            {
                style.Background = (byte)(1 + code - 40);
                style.ExtendedBackground = 0;
            }
            else if (code >= 90 && code <= 97)
            {
                // Bright colours are palette entries 8..15; they no longer turn bold on, which used to stick
                // to the text written after a later normal colour.
                style.Foreground = 0;
                style.ExtendedForeground = EncodeExtendedColor(8 + code - 90);
            }
            else if (code >= 100 && code <= 107)
            {
                style.Background = 0;
                style.ExtendedBackground = EncodeExtendedColor(8 + code - 100);
            }
            else
            {
                switch (code)
                {
                    case 0:
                        style = CellStyle.Default;
                        break;
                    case 1:
                        style.Bold = true;
                        break;
                    case 21:
                    case 22:
                        style.Bold = false;
                        break;
                    case 4:
                        style.Underline = true;
                        break;
                    case 24:
                        style.Underline = false;
                        break;
                    case 7:
                        style.Inverse = true;
                        break;
                    case 27:
                        style.Inverse = false;
                        break;
                    case 38:
                        style.Foreground = 0;
                        style.ExtendedForeground = EncodeExtendedColor(attribute.Color);
                        break;
                    case 39:
                        style.Foreground = 0;
                        style.ExtendedForeground = 0;
                        break;
                    case 48:
                        style.Background = 0;
                        style.ExtendedBackground = EncodeExtendedColor(attribute.Color);
                        break;
                    case 49:
                        style.Background = 0;
                        style.ExtendedBackground = 0;
                        break;
                }
            }
        }
    }

    // See CellStyle.ExtendedForeground: palette indices are stored + 1 so that 0 can mean "not set".
    private static int EncodeExtendedColor(int color)
    {
        return ((color & XtermProtocol.RgbColorFlag) != 0) ? color : (color + 1);
    }
}
