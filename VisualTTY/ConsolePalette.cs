using SmarTTY.TextEditing;
using VisualTTY.DeviceTerminal;

namespace VisualTTY;

public class ConsolePalette
{
    public DeviceTerminalSettings.ColorWrapper[] NormalColors;

    public DeviceTerminalSettings.ColorWrapper[] BrightColors;

    public static readonly ConsolePalette PuTTY = new ConsolePalette
    {
        NormalColors = new DeviceTerminalSettings.ColorWrapper[8]
        {
            RgbColor.FromArgb(0, 0, 0),
            RgbColor.FromArgb(187, 0, 0),
            RgbColor.FromArgb(0, 187, 0),
            RgbColor.FromArgb(187, 187, 0),
            RgbColor.FromArgb(0, 0, 187),
            RgbColor.FromArgb(187, 0, 187),
            RgbColor.FromArgb(0, 187, 187),
            RgbColor.FromArgb(187, 187, 187)
        },
        BrightColors = new DeviceTerminalSettings.ColorWrapper[8]
        {
            RgbColor.FromArgb(85, 85, 85),
            RgbColor.FromArgb(255, 85, 85),
            RgbColor.FromArgb(85, 255, 85),
            RgbColor.FromArgb(255, 255, 85),
            RgbColor.FromArgb(85, 85, 255),
            RgbColor.FromArgb(255, 85, 255),
            RgbColor.FromArgb(85, 255, 255),
            RgbColor.FromArgb(255, 255, 255)
        }
    };

    public ConsolePalette Clone()
    {
        return new ConsolePalette
        {
            BrightColors = (DeviceTerminalSettings.ColorWrapper[])BrightColors.Clone(),
            NormalColors = (DeviceTerminalSettings.ColorWrapper[])NormalColors.Clone()
        };
    }
}
