using System;
using System.Collections.Generic;
using SmarTTY.TextEditing;
using VisualTTY.DeviceTerminal;

namespace VisualTTY;

/// <summary>
/// A ready-made colour scheme of the terminal, as the popular terminals ship them: the background, the colour of text without a
/// colour of its own, the colour of bold text without one, and the 16 colours of the ANSI palette.
/// </summary>
public sealed class TerminalColorPreset
{
    private readonly uint[] _normal;

    private readonly uint[] _bright;

    private TerminalColorPreset(string name, uint background, uint foreground, uint boldForeground, uint[] normal, uint[] bright)
    {
        if (normal.Length != 8 || bright.Length != 8)
        {
            throw new ArgumentException("A palette has 8 normal and 8 bright colours.");
        }
        Name = name;
        Background = Opaque(background);
        Foreground = Opaque(foreground);
        BoldForeground = Opaque(boldForeground);
        _normal = normal;
        _bright = bright;
    }

    /// <summary>The name shown in the settings.</summary>
    public string Name { get; }

    /// <summary>The background of the terminal.</summary>
    public RgbColor Background { get; }

    /// <summary>The colour of text without a colour of its own.</summary>
    public RgbColor Foreground { get; }

    /// <summary>The colour of bold text without a colour of its own.</summary>
    public RgbColor BoldForeground { get; }

    /// <summary>The 16 colours of the palette as the settings keep them.</summary>
    public ConsolePalette ToPalette()
    {
        DeviceTerminalSettings.ColorWrapper[] normal = new DeviceTerminalSettings.ColorWrapper[8];
        DeviceTerminalSettings.ColorWrapper[] bright = new DeviceTerminalSettings.ColorWrapper[8];
        for (int index = 0; index < 8; index++)
        {
            normal[index] = Opaque(_normal[index]);
            bright[index] = Opaque(_bright[index]);
        }
        return new ConsolePalette { NormalColors = normal, BrightColors = bright };
    }

    /// <summary>The name of the scheme that keeps the colours SmarTTY always had (black, light grey, the PuTTY palette).</summary>
    public const string DefaultName = "SmarTTY (PuTTY)";

    /// <summary>The schemes the settings offer, the default first.</summary>
    public static IReadOnlyList<TerminalColorPreset> All { get; } = new[]
    {
        new TerminalColorPreset(DefaultName, 0x000000, 0xD3D3D3, 0xFFFFFF,
            new uint[] { 0x000000, 0xBB0000, 0x00BB00, 0xBBBB00, 0x0000BB, 0xBB00BB, 0x00BBBB, 0xBBBBBB },
            new uint[] { 0x555555, 0xFF5555, 0x55FF55, 0xFFFF55, 0x5555FF, 0xFF55FF, 0x55FFFF, 0xFFFFFF }),
        // GNOME Terminal on Ubuntu: the aubergine background and the Ubuntu palette.
        new TerminalColorPreset("Ubuntu", 0x300A24, 0xFFFFFF, 0xFFFFFF,
            new uint[] { 0x171421, 0xC01C28, 0x26A269, 0xA2734C, 0x12488B, 0xA347BA, 0x2AA1B3, 0xD0CFCC },
            new uint[] { 0x5E5C64, 0xF66151, 0x33DA7A, 0xE9AD0C, 0x2A7BDE, 0xC061CB, 0x33C7DE, 0xFFFFFF }),
        // The older Ubuntu look: aubergine with the Tango palette.
        new TerminalColorPreset("Ubuntu (Tango)", 0x300A24, 0xEEEEEC, 0xFFFFFF,
            new uint[] { 0x2E3436, 0xCC0000, 0x4E9A06, 0xC4A000, 0x3465A4, 0x75507B, 0x06989A, 0xD3D7CF },
            new uint[] { 0x555753, 0xEF2929, 0x8AE234, 0xFCE94F, 0x729FCF, 0xAD7FA8, 0x34E2E2, 0xEEEEEC }),
        new TerminalColorPreset("Tango Dark", 0x2E3436, 0xD3D7CF, 0xEEEEEC,
            new uint[] { 0x2E3436, 0xCC0000, 0x4E9A06, 0xC4A000, 0x3465A4, 0x75507B, 0x06989A, 0xD3D7CF },
            new uint[] { 0x555753, 0xEF2929, 0x8AE234, 0xFCE94F, 0x729FCF, 0xAD7FA8, 0x34E2E2, 0xEEEEEC }),
        // Windows Terminal.
        new TerminalColorPreset("Campbell", 0x0C0C0C, 0xCCCCCC, 0xF2F2F2,
            new uint[] { 0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC },
            new uint[] { 0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2 }),
        new TerminalColorPreset("One Half Dark", 0x282C34, 0xDCDFE4, 0xFFFFFF,
            new uint[] { 0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4 },
            new uint[] { 0x5A6374, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4 }),
        new TerminalColorPreset("xterm", 0x000000, 0xE5E5E5, 0xFFFFFF,
            new uint[] { 0x000000, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5 },
            new uint[] { 0x7F7F7F, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF }),
        new TerminalColorPreset("Solarized Dark", 0x002B36, 0x839496, 0x93A1A1,
            new uint[] { 0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5 },
            new uint[] { 0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3 }),
        new TerminalColorPreset("Solarized Light", 0xFDF6E3, 0x657B83, 0x586E75,
            new uint[] { 0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5 },
            new uint[] { 0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3 }),
        new TerminalColorPreset("Dracula", 0x282A36, 0xF8F8F2, 0xFFFFFF,
            new uint[] { 0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2 },
            new uint[] { 0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF }),
        new TerminalColorPreset("Monokai", 0x272822, 0xF8F8F2, 0xF9F8F5,
            new uint[] { 0x272822, 0xF92672, 0xA6E22E, 0xF4BF75, 0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF8F8F2 },
            new uint[] { 0x75715E, 0xF92672, 0xA6E22E, 0xF4BF75, 0x66D9EF, 0xAE81FF, 0xA1EFE4, 0xF9F8F5 }),
        new TerminalColorPreset("Gruvbox Dark", 0x282828, 0xEBDBB2, 0xFBF1C7,
            new uint[] { 0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984 },
            new uint[] { 0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2 }),
        new TerminalColorPreset("Nord", 0x2E3440, 0xD8DEE9, 0xECEFF4,
            new uint[] { 0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0 },
            new uint[] { 0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4 }),
        // macOS Terminal, "Basic" profile.
        new TerminalColorPreset("macOS Basic", 0xFFFFFF, 0x000000, 0x000000,
            new uint[] { 0x000000, 0x990000, 0x00A600, 0x999900, 0x0000B2, 0xB200B2, 0x00A6B2, 0xBFBFBF },
            new uint[] { 0x666666, 0xE50000, 0x00D900, 0xE5E500, 0x0000FF, 0xE500E5, 0x00E5E5, 0xE5E5E5 }),
    };

    /// <summary>The scheme of this name (the case does not matter), or null.</summary>
    public static TerminalColorPreset Find(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        foreach (TerminalColorPreset preset in All)
        {
            if (string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }
        return null;
    }

    public override string ToString() => Name;

    private static RgbColor Opaque(uint rgb)
    {
        return RgbColor.FromArgb(255, (int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF));
    }
}
