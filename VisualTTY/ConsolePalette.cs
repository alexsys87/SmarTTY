using System.Drawing;
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
            Color.FromArgb(0, 0, 0),
            Color.FromArgb(187, 0, 0),
            Color.FromArgb(0, 187, 0),
            Color.FromArgb(187, 187, 0),
            Color.FromArgb(0, 0, 187),
            Color.FromArgb(187, 0, 187),
            Color.FromArgb(0, 187, 187),
            Color.FromArgb(187, 187, 187)
        },
        BrightColors = new DeviceTerminalSettings.ColorWrapper[8]
        {
            Color.FromArgb(85, 85, 85),
            Color.FromArgb(255, 85, 85),
            Color.FromArgb(85, 255, 85),
            Color.FromArgb(255, 255, 85),
            Color.FromArgb(85, 85, 255),
            Color.FromArgb(255, 85, 255),
            Color.FromArgb(85, 255, 255),
            Color.FromArgb(255, 255, 255)
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
