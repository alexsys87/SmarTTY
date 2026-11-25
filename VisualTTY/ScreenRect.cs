using System;
using System.Xml.Serialization;

namespace VisualTTY;

/// <summary>
/// A rectangle of the screen in pixels, kept in the settings (the placement of a window and the monitors it was remembered on). Until stage
/// 5C the settings held a <c>System.Drawing.Rectangle</c>; this struct is written to XML as that one was (an array item is a
/// <c>Rectangle</c> element; the elements <c>Location</c> with <c>X</c> and <c>Y</c>, <c>Size</c> with <c>Width</c> and <c>Height</c>, then
/// <c>X</c>, <c>Y</c>, <c>Width</c>, <c>Height</c>), so a settings file reads the same either way. <c>tests/ScreenRectXmlTests</c> checks it.
/// </summary>
[XmlType("Rectangle")]
public struct ScreenRect : IEquatable<ScreenRect>
{
    /// <summary>The corner of a rectangle in the XML (the <c>Location</c> of <c>System.Drawing.Rectangle</c>).</summary>
    public struct Corner
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    /// <summary>The size of a rectangle in the XML (the <c>Size</c> of <c>System.Drawing.Rectangle</c>).</summary>
    public struct Extent
    {
        public int Width { get; set; }

        public int Height { get; set; }
    }

    public ScreenRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The rectangle from its edges.</summary>
    public static ScreenRect FromLTRB(int left, int top, int right, int bottom)
    {
        return new ScreenRect(left, top, right - left, bottom - top);
    }

    /// <summary>No rectangle (all zero).</summary>
    public static readonly ScreenRect Empty = default(ScreenRect);

    /// <summary>The corner, as the XML writes it first; setting it sets <see cref="X"/> and <see cref="Y"/>.</summary>
    public Corner Location
    {
        get => new Corner { X = X, Y = Y };
        set
        {
            X = value.X;
            Y = value.Y;
        }
    }

    /// <summary>The size, as the XML writes it second; setting it sets <see cref="Width"/> and <see cref="Height"/>.</summary>
    public Extent Size
    {
        get => new Extent { Width = Width, Height = Height };
        set
        {
            Width = value.Width;
            Height = value.Height;
        }
    }

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    // The edges have no setter, so they are not written to the XML.
    public int Left => X;

    public int Top => Y;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Equals(ScreenRect other)
    {
        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    }

    public override bool Equals(object obj)
    {
        return obj is ScreenRect other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((X * 397 ^ Y) * 397 ^ Width) * 397 ^ Height;
        }
    }

    public static bool operator ==(ScreenRect left, ScreenRect right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ScreenRect left, ScreenRect right)
    {
        return !left.Equals(right);
    }

    public override string ToString()
    {
        return "{X=" + X + ",Y=" + Y + ",Width=" + Width + ",Height=" + Height + "}";
    }
}
