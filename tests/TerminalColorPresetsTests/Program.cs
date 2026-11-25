using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.TextEditing;
using SmarTTY.Utilities;
using VisualTTY;
using VisualTTY.DeviceTerminal;

// Checks the colour schemes of the terminal (VisualTTY/TerminalColorPresets.cs) and the opacity of its background.
// Run with: dotnet run --project tests/TerminalColorPresetsTests -c Release
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(equal, name + (equal ? "" : $" (expected '{expected}', got '{actual}')"));
    }

    private static uint Rgb(RgbColor color) => color.Argb & 0xFFFFFF;

    private static void CheckEverySchemeIsComplete()
    {
        Check(TerminalColorPreset.All.Count >= 10, "there are ten schemes or more");
        foreach (TerminalColorPreset preset in TerminalColorPreset.All)
        {
            Check(!string.IsNullOrWhiteSpace(preset.Name), "a scheme has a name");
            Equal(preset.Background.A, (byte)255, preset.Name + ": the background is opaque");
            Equal(preset.Foreground.A, (byte)255, preset.Name + ": the text colour is opaque");
            Equal(preset.BoldForeground.A, (byte)255, preset.Name + ": the bold text colour is opaque");
            Check(Rgb(preset.Background) != Rgb(preset.Foreground), preset.Name + ": the text is not the colour of the background");
            ConsolePalette palette = preset.ToPalette();
            Equal(palette.NormalColors.Length, 8, preset.Name + ": 8 normal colours");
            Equal(palette.BrightColors.Length, 8, preset.Name + ": 8 bright colours");
            Check(palette.NormalColors.Concat(palette.BrightColors).All(color => color.Alpha == 255), preset.Name + ": every palette colour is opaque");
            // Each call gives a palette of its own: the settings window may change the one it got.
            ConsolePalette again = preset.ToPalette();
            again.NormalColors[0] = RgbColor.FromArgb(255, 1, 2, 3);
            Equal(Rgb(preset.ToPalette().NormalColors[0]), Rgb(palette.NormalColors[0]), preset.Name + ": a palette handed out is a copy");
            Check(!ReferenceEquals(palette.NormalColors, again.NormalColors), preset.Name + ": two palettes are two arrays");
        }
    }

    private static void CheckNames()
    {
        string[] names = TerminalColorPreset.All.Select(preset => preset.Name).ToArray();
        Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Length, "the names are unique (the case does not matter)");
        foreach (string name in names)
        {
            Check(ReferenceEquals(TerminalColorPreset.Find(name), TerminalColorPreset.All.First(preset => preset.Name == name)), "found again: " + name);
            Check(TerminalColorPreset.Find(name.ToUpperInvariant()) != null, "found in other case: " + name);
        }
        Check(TerminalColorPreset.Find(null) == null, "no name finds nothing");
        Check(TerminalColorPreset.Find("") == null, "an empty name finds nothing");
        Check(TerminalColorPreset.Find("No such scheme") == null, "an unknown name finds nothing");
        Equal(TerminalColorPreset.All[0].Name, TerminalColorPreset.DefaultName, "the default scheme is the first");
        Check(TerminalColorPreset.Find("Ubuntu") != null, "there is an Ubuntu scheme");
        Check(TerminalColorPreset.Find("Campbell") != null, "there is the scheme of Windows Terminal");
    }

    private static void CheckUbuntu()
    {
        TerminalColorPreset ubuntu = TerminalColorPreset.Find("Ubuntu");
        Equal(Rgb(ubuntu.Background), 0x300A24u, "Ubuntu: the aubergine background");
        Equal(Rgb(ubuntu.Foreground), 0xFFFFFFu, "Ubuntu: white text");
        Equal(Rgb(ubuntu.ToPalette().NormalColors[1]), 0xC01C28u, "Ubuntu: red of the Ubuntu palette");
    }

    // The default scheme is what the terminal showed before there were schemes: choosing it changes nothing.
    private static void CheckDefaultIsUnchanged()
    {
        TerminalColorPreset preset = TerminalColorPreset.Find(TerminalColorPreset.DefaultName);
        Equal(Rgb(preset.Background), 0x000000u, "default: black background (TerminalColorScheme.DefaultBackground)");
        Equal(Rgb(preset.Foreground), 0xD3D3D3u, "default: light grey text (TerminalColorScheme.DefaultForeground)");
        Equal(Rgb(preset.BoldForeground), 0xFFFFFFu, "default: white bold text (TerminalColorScheme.BoldDefaultForeground)");
        ConsolePalette palette = preset.ToPalette();
        for (int index = 0; index < 8; index++)
        {
            Equal(Rgb(palette.NormalColors[index]), Rgb(ConsolePalette.PuTTY.NormalColors[index]), "default: normal colour " + index + " of PuTTY");
            Equal(Rgb(palette.BrightColors[index]), Rgb(ConsolePalette.PuTTY.BrightColors[index]), "default: bright colour " + index + " of PuTTY");
        }
    }

    private static void CheckPaletteXml()
    {
        foreach (TerminalColorPreset preset in TerminalColorPreset.All)
        {
            ConsolePalette palette = preset.ToPalette();
            ConsolePalette back = XmlStorage.Deserialize<ConsolePalette>(XmlStorage.Serialize(palette));
            bool same = Enumerable.Range(0, 8).All(index =>
                ((RgbColor)back.NormalColors[index]).Argb == ((RgbColor)palette.NormalColors[index]).Argb
                && ((RgbColor)back.BrightColors[index]).Argb == ((RgbColor)palette.BrightColors[index]).Argb);
            Check(same, preset.Name + ": the palette is read back from XML as it was written");
        }
        // A colour that was never set (the default of a new settings file) has no alpha: the terminal keeps its own colour then.
        DeviceTerminalSettings.ColorWrapper unset = default;
        Equal(unset.Alpha, 0, "a colour never set has no alpha");
    }

    private static void CheckOpacity()
    {
        Equal(WindowChromeRules.OpacityToAlpha(100), (byte)255, "100 % is an opaque background");
        Equal(WindowChromeRules.OpacityToAlpha(70), (byte)178, "70 % is alpha 178 (178.5 rounds to even)");
        Equal(WindowChromeRules.OpacityToAlpha(0), WindowChromeRules.OpacityToAlpha(30), "below 30 % is 30 %");
    }

    private static int Main()
    {
        try
        {
            CheckEverySchemeIsComplete();
            CheckNames();
            CheckUbuntu();
            CheckDefaultIsUnchanged();
            CheckPaletteXml();
            CheckOpacity();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine(_passed + " checks passed");
        return 0;
    }
}
