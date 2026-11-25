using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using SmarTTY.Utilities;
using VisualTTY;

// The shape of the preferences that matter here: the field names and types are those of CommonGUIPreferences.
[XmlRoot("CommonGUIPreferences")]
public class PreferencesNow
{
    public bool FullScreen;

    public List<RememberedWindowSize> WindowSizes = new List<RememberedWindowSize>();

    public int FileListWidth;
}

// A preferences file of the version before the window sizes.
[XmlRoot("CommonGUIPreferences")]
public class PreferencesBefore
{
    public bool FullScreen;
}

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

    private static void Equal<T>(T expected, T actual, string name)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), name + " (expected " + expected + ", got " + actual + ")");
    }

    private static T RoundTrip<T>(T value) where T : class
    {
        XmlSerializer serializer = new XmlSerializer(typeof(T));
        using (StringWriter writer = new StringWriter())
        {
            serializer.Serialize(writer, value);
            using (StringReader reader = new StringReader(writer.ToString()))
            {
                return (T)serializer.Deserialize(reader);
            }
        }
    }

    private static int Main()
    {
        Opacity();
        TitleBarColors();
        Lengths();
        Centering();
        Sizes();
        Xml();
        Console.WriteLine("All " + _passed + " window chrome checks passed.");
        return 0;
    }

    private static void Opacity()
    {
        Equal(100, WindowChromeRules.ClampOpacityPercent(100), "100 % stays");
        Equal(30, WindowChromeRules.ClampOpacityPercent(30), "30 % stays");
        Equal(30, WindowChromeRules.ClampOpacityPercent(0), "0 % is the least");
        Equal(30, WindowChromeRules.ClampOpacityPercent(-50), "a negative value is the least");
        Equal(100, WindowChromeRules.ClampOpacityPercent(250), "past 100 % is 100 %");
        Equal(100, WindowChromeRules.ClampOpacityPercent(int.MaxValue), "a huge value is 100 %");
        Equal((byte)255, WindowChromeRules.OpacityToAlpha(100), "100 % is alpha 255 (no layered style)");
        Equal((byte)128, WindowChromeRules.OpacityToAlpha(50), "50 % is alpha 128");
        Equal((byte)76, WindowChromeRules.OpacityToAlpha(30), "30 % is alpha 76 (76.5 rounds to even)");
        Equal(WindowChromeRules.OpacityToAlpha(30), WindowChromeRules.OpacityToAlpha(1), "below the floor is the floor");
        Equal((byte)255, WindowChromeRules.OpacityToAlpha(1000), "above 100 % is opaque");
        byte previous = 0;
        bool rising = true;
        for (int percent = 30; percent <= 100; percent++)
        {
            byte alpha = WindowChromeRules.OpacityToAlpha(percent);
            rising &= alpha >= previous;
            previous = alpha;
        }
        Check(rising, "alpha never falls as the opacity grows");
    }

    private static void TitleBarColors()
    {
        Equal(0x00332211, WindowChromeRules.ToColorRef(0x11, 0x22, 0x33), "COLORREF is BGR");
        Equal(0x00FFFFFF, WindowChromeRules.ToColorRef(255, 255, 255), "white");
        Equal(0, WindowChromeRules.ToColorRef(0, 0, 0), "black");
        Check(WindowChromeRules.IsDark(0x1E, 0x1F, 0x22), "the Dark theme window background is dark");
        Check(WindowChromeRules.IsDark(0x0D, 0x1B, 0x2A), "the Blue theme window background is dark");
        Check(!WindowChromeRules.IsDark(255, 255, 255), "white is light");
        Check(!WindowChromeRules.IsDark(0xF3, 0xF3, 0xF3), "a light grey is light");
        Check(WindowChromeRules.IsDark(0, 0, 0), "black is dark");
        Check(!WindowChromeRules.IsDark(0, 255, 0), "green is bright");
        Check(WindowChromeRules.IsDark(0, 0, 255), "pure blue is dark");
        Check(WindowChromeRules.IsDark(0x66, 0x66, 0x66), "a mid grey just below half brightness is dark");
        Check(!WindowChromeRules.IsDark(0x99, 0x99, 0x99), "a mid grey above half brightness is light");
    }

    private static void Lengths()
    {
        Equal(500.0, WindowChromeRules.ClampLength(500, 100, double.PositiveInfinity, 1920), "a length that fits is kept");
        Equal(1920.0, WindowChromeRules.ClampLength(5000, 100, double.PositiveInfinity, 1920), "a length past the monitor is cut to it");
        Equal(800.0, WindowChromeRules.ClampLength(5000, 100, 800, 1920), "a length past MaxWidth is cut to it");
        Equal(300.0, WindowChromeRules.ClampLength(50, 300, double.PositiveInfinity, 1920), "a length under MinWidth is raised to it");
        Equal(2000.0, WindowChromeRules.ClampLength(100, 2000, double.PositiveInfinity, 1920), "a MinWidth past the monitor still wins");
        Equal(700.0, WindowChromeRules.ClampLength(700, 100, double.PositiveInfinity, 0), "no monitor size known: only the window limits count");
        Check(double.IsNaN(WindowChromeRules.ClampLength(0, 100, 1000, 1920)), "0 is nothing to apply");
        Check(double.IsNaN(WindowChromeRules.ClampLength(-5, 100, 1000, 1920)), "a negative length is nothing to apply");
        Check(double.IsNaN(WindowChromeRules.ClampLength(double.NaN, 100, 1000, 1920)), "NaN is nothing to apply");
        Check(double.IsNaN(WindowChromeRules.ClampLength(double.PositiveInfinity, 100, 1000, 1920)), "infinity is nothing to apply");
        Equal(640.0, WindowChromeRules.ClampLength(640, double.NaN, double.NaN, 1920), "NaN limits are no limits");
    }

    private static void Centering()
    {
        Equal(400.0, WindowChromeRules.CenteredStart(500, 200, 0, 1920), "the middle is kept");
        Equal(0.0, WindowChromeRules.CenteredStart(50, 400, 0, 1920), "a window that would start left of the monitor starts at its edge");
        Equal(1520.0, WindowChromeRules.CenteredStart(1900, 400, 0, 1920), "a window that would end past the monitor ends at its edge");
        Equal(1520.0, WindowChromeRules.CenteredStart(1720.5, 400, 0, 1920), "half a pixel past the end is brought back");
        Equal(-1920.0, WindowChromeRules.CenteredStart(-1000, 3000, -1920, 0), "wider than the monitor: the start of the monitor");
        Equal(-1500.0, WindowChromeRules.CenteredStart(-1300, 400, -1920, 0), "a monitor at negative coordinates");
    }

    private static void Sizes()
    {
        List<RememberedWindowSize> list = new List<RememberedWindowSize>();
        Check(!WindowSizeList.TryGet(list, "A", out int w, out int h) && w == 0 && h == 0, "an empty list has nothing");
        Check(!WindowSizeList.TryGet(null, "A", out w, out h), "a null list has nothing");
        Check(!WindowSizeList.TryGet(list, null, out w, out h), "a null key has nothing");
        Check(WindowSizeList.Set(list, "A", 640, 480), "a size is set");
        Check(WindowSizeList.TryGet(list, "A", out w, out h) && w == 640 && h == 480, "and found");
        Check(!WindowSizeList.TryGet(list, "a", out w, out h), "the key is case sensitive (type names are)");
        Check(WindowSizeList.Set(list, "A", 700, 500) && list.Count == 1, "a second set of the same window replaces the entry");
        Check(WindowSizeList.TryGet(list, "A", out w, out h) && w == 700 && h == 500, "with the new size");
        WindowSizeList.Set(list, "B", 300, 200);
        WindowSizeList.Set(list, "A", 710, 510);
        Equal("B", list[0].Key, "the entry set last goes to the end");
        Equal("A", list[1].Key, "and the older one is first");
        Check(!WindowSizeList.Set(list, "C", 0, 100), "a zero width is refused");
        Check(!WindowSizeList.Set(list, "C", 100, -1), "a negative height is refused");
        Check(!WindowSizeList.Set(list, "", 100, 100), "an empty key is refused");
        Check(!WindowSizeList.Set(list, null, 100, 100), "a null key is refused");
        Check(!WindowSizeList.Set(null, "C", 100, 100), "a null list is refused");
        Equal(2, list.Count, "a refused set changes nothing");
        list.Add(new RememberedWindowSize { Key = "Zero", Width = 0, Height = 10 });
        Check(!WindowSizeList.TryGet(list, "Zero", out w, out h), "an entry of a hand-edited file with a zero width is not used");
        list.Add(null);
        Check(WindowSizeList.Set(list, "D", 10, 10) && !list.Contains(null), "a null entry in the list is dropped by the next set");

        List<RememberedWindowSize> many = new List<RememberedWindowSize>();
        for (int i = 0; i < WindowSizeList.MaxEntries + 25; i++)
        {
            WindowSizeList.Set(many, "W" + i, 100 + i, 100);
        }
        Equal(WindowSizeList.MaxEntries, many.Count, "the list is kept to its limit");
        Check(!WindowSizeList.TryGet(many, "W0", out w, out h), "the oldest entry went first");
        Check(WindowSizeList.TryGet(many, "W" + (WindowSizeList.MaxEntries + 24), out w, out h), "the newest stayed");
    }

    private static void Xml()
    {
        PreferencesNow now = new PreferencesNow { FullScreen = true, FileListWidth = 333 };
        WindowSizeList.Set(now.WindowSizes, "SmarTTY.GUI.SocksProxyWindow", 620, 520);
        WindowSizeList.Set(now.WindowSizes, "SmarTTY.GUI.FileListWindow", 900, 700);
        PreferencesNow back = RoundTrip(now);
        Equal(2, back.WindowSizes.Count, "both sizes come back from XML");
        Check(WindowSizeList.TryGet(back.WindowSizes, "SmarTTY.GUI.SocksProxyWindow", out int w, out int h) && w == 620 && h == 520, "a size read from XML");
        Equal(333, back.FileListWidth, "the file manager width comes back");
        Check(back.FullScreen, "the other fields are not disturbed");

        // A file of the version before: the new members are not there and keep their defaults.
        string old = "<?xml version=\"1.0\" encoding=\"utf-8\"?><CommonGUIPreferences><FullScreen>true</FullScreen></CommonGUIPreferences>";
        PreferencesNow read;
        using (StringReader reader = new StringReader(old))
        {
            read = (PreferencesNow)new XmlSerializer(typeof(PreferencesNow)).Deserialize(reader);
        }
        Check(read.FullScreen && read.WindowSizes != null && read.WindowSizes.Count == 0 && read.FileListWidth == 0, "an old file has no sizes and no file manager width");

        // A file of the new version read by the old program (a downgrade): the unknown elements are ignored.
        using (StringWriter writer = new StringWriter())
        {
            new XmlSerializer(typeof(PreferencesNow)).Serialize(writer, now);
            using (StringReader reader = new StringReader(writer.ToString()))
            {
                PreferencesBefore before = (PreferencesBefore)new XmlSerializer(typeof(PreferencesBefore)).Deserialize(reader);
                Check(before.FullScreen, "the old program reads a file with the new members");
            }
        }
    }
}
