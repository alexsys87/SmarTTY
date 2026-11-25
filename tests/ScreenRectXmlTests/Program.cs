using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using VisualTTY;

// The window placement of the settings with the type it had until stage 5C and with the type it has now; the field names are those of
// CommonGUIPreferences.WindowPlacement, and both are written under the same root element.
[XmlRoot("WindowPlacement")]
public class OldPlacement
{
    public Rectangle LastKnownConnectionWindowRectangle;

    public Rectangle[] ScreenRectanglesWhenWindowRectangleRemembered;
}

[XmlRoot("WindowPlacement")]
public class NewPlacement
{
    public ScreenRect LastKnownConnectionWindowRectangle;

    public ScreenRect[] ScreenRectanglesWhenWindowRectangleRemembered;
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

    private static void Equal<T>(T actual, T expected, string name)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(same, name + (same ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private static string Write<T>(T value)
    {
        XmlSerializer serializer = new XmlSerializer(typeof(T));
        StringBuilder text = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true }))
        {
            serializer.Serialize(writer, value);
        }
        return text.ToString();
    }

    private static T Read<T>(string xml)
    {
        using (StringReader reader = new StringReader(xml))
        {
            return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
    }

    private static ScreenRect ToNew(Rectangle r)
    {
        return new ScreenRect(r.X, r.Y, r.Width, r.Height);
    }

    private static ScreenRect[] ToNew(Rectangle[] items)
    {
        if (items == null)
        {
            return null;
        }
        ScreenRect[] result = new ScreenRect[items.Length];
        for (int index = 0; index < items.Length; index++)
        {
            result[index] = ToNew(items[index]);
        }
        return result;
    }

    private static void Case(string name, Rectangle window, Rectangle[] screens)
    {
        OldPlacement old = new OldPlacement { LastKnownConnectionWindowRectangle = window, ScreenRectanglesWhenWindowRectangleRemembered = screens };
        NewPlacement current = new NewPlacement { LastKnownConnectionWindowRectangle = ToNew(window), ScreenRectanglesWhenWindowRectangleRemembered = ToNew(screens) };
        string oldXml = Write(old);
        string newXml = Write(current);
        Equal(newXml, oldXml, name + ": the XML is the same");

        NewPlacement readNew = Read<NewPlacement>(oldXml);
        Equal(readNew.LastKnownConnectionWindowRectangle, ToNew(window), name + ": the old XML gives the same window rectangle");
        Equal(Describe(readNew.ScreenRectanglesWhenWindowRectangleRemembered), Describe(ToNew(screens)), name + ": the old XML gives the same monitors");

        OldPlacement readOld = Read<OldPlacement>(newXml);
        Equal(readOld.LastKnownConnectionWindowRectangle, window, name + ": the new XML reads back as the old rectangle");
        Equal(Describe(ToNew(readOld.ScreenRectanglesWhenWindowRectangleRemembered)), Describe(ToNew(screens)), name + ": the new XML reads back as the old monitors");
    }

    private static string Describe(ScreenRect[] items)
    {
        if (items == null)
        {
            return "null";
        }
        return "[" + string.Join(" ", Array.ConvertAll(items, item => item.ToString())) + "]";
    }

    private static void Main()
    {
        Case("one monitor", new Rectangle(100, 80, 900, 600), new[] { new Rectangle(0, 0, 1920, 1080) });
        Case("two monitors, one left of the main one", new Rectangle(-1500, 20, 1200, 700), new[] { new Rectangle(0, 0, 2560, 1440), new Rectangle(-1920, -200, 1920, 1200) });
        Case("nothing remembered", Rectangle.Empty, null);
        Case("no monitors", new Rectangle(1, 2, 3, 4), new Rectangle[0]);

        // The XML of one rectangle element by element, as the settings file has it.
        string xml = Write(new NewPlacement { LastKnownConnectionWindowRectangle = new ScreenRect(5, 6, 7, 8) });
        Check(xml.Contains("<Location>") && xml.Contains("<Size>"), "a rectangle is written with its Location and Size");
        Check(xml.IndexOf("<Location>", StringComparison.Ordinal) < xml.IndexOf("<Size>", StringComparison.Ordinal), "Location comes before Size");
        Check(!xml.Contains("<Left>") && !xml.Contains("<Right>") && !xml.Contains("<Top>") && !xml.Contains("<Bottom>"), "the edges are not written");
        string screens = Write(new NewPlacement { ScreenRectanglesWhenWindowRectangleRemembered = new[] { new ScreenRect(0, 0, 10, 10) } });
        Check(screens.Contains("<Rectangle>"), "an item of the monitors is a Rectangle element");

        // A rectangle from its edges and the edges of a rectangle, as the monitors and the terminals use them.
        ScreenRect edges = ScreenRect.FromLTRB(-10, 20, 30, 70);
        Equal(edges, new ScreenRect(-10, 20, 40, 50), "FromLTRB gives the position and the size");
        Equal(edges.Right, Rectangle.FromLTRB(-10, 20, 30, 70).Right, "Right is that of Rectangle");
        Equal(edges.Bottom, Rectangle.FromLTRB(-10, 20, 30, 70).Bottom, "Bottom is that of Rectangle");
        Equal(edges.Left, -10, "Left is X");
        Equal(edges.Top, 20, "Top is Y");
        Equal(ScreenRect.Empty, new ScreenRect(0, 0, 0, 0), "Empty is all zero");
        Check(new ScreenRect(1, 2, 3, 4) != new ScreenRect(1, 2, 3, 5), "rectangles that differ are not equal");
        Equal(new ScreenRect(1, 2, 3, 4).ToString(), new Rectangle(1, 2, 3, 4).ToString(), "the text is that of Rectangle");

        Console.WriteLine();
        Console.WriteLine("All " + _passed + " screen rectangle checks passed.");
    }
}
