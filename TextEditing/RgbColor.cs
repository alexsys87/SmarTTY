using System;
using System.Globalization;

namespace SmarTTY.TextEditing;

/// <summary>
/// An ARGB colour for code that must not depend on the GDI+ colour type. <see cref="Empty"/> means "not set", like the
/// empty colour of GDI+; it is the default value, so a zeroed array of colours is an array of unset colours. Unlike the
/// GDI+ type it compares only the four components: a named colour and the same colour built from its components are equal.
/// </summary>
public readonly struct RgbColor : IEquatable<RgbColor>
{
    /// <summary>Not set. The value of <c>default(RgbColor)</c>; also a fully transparent black.</summary>
    public static readonly RgbColor Empty = new RgbColor(0u);

    /// <summary>The colour as 0xAARRGGBB.</summary>
    public readonly uint Argb;

    /// <summary>Builds a colour from 0xAARRGGBB.</summary>
    public RgbColor(uint argb)
    {
        Argb = argb;
    }

    /// <summary>Builds an opaque colour from its red, green and blue components (0..255).</summary>
    public static RgbColor FromArgb(int red, int green, int blue)
    {
        return FromArgb(255, red, green, blue);
    }

    /// <summary>Builds a colour from its alpha, red, green and blue components (0..255).</summary>
    public static RgbColor FromArgb(int alpha, int red, int green, int blue)
    {
        CheckComponent(alpha, "alpha");
        CheckComponent(red, "red");
        CheckComponent(green, "green");
        CheckComponent(blue, "blue");
        return new RgbColor((uint)(alpha << 24 | red << 16 | green << 8 | blue));
    }

    /// <summary>Builds a colour from the value of <c>ToArgb()</c> of the GDI+ colour type (0xAARRGGBB as a signed number).</summary>
    public static RgbColor FromArgb(int argb)
    {
        return new RgbColor(unchecked((uint)argb));
    }

    /// <summary>The colour as a signed 0xAARRGGBB, like <c>ToArgb()</c> of the GDI+ colour type.</summary>
    public int ToArgb()
    {
        return unchecked((int)Argb);
    }

    /// <summary>True when the colour is not set.</summary>
    public bool IsEmpty => Argb == 0u;

    /// <summary>Alpha component (255 is opaque).</summary>
    public byte A => (byte)(Argb >> 24);

    /// <summary>Red component.</summary>
    public byte R => (byte)(Argb >> 16);

    /// <summary>Green component.</summary>
    public byte G => (byte)(Argb >> 8);

    /// <summary>Blue component.</summary>
    public byte B => (byte)Argb;

    public bool Equals(RgbColor other)
    {
        return Argb == other.Argb;
    }

    public override bool Equals(object obj)
    {
        return obj is RgbColor other && Equals(other);
    }

    public override int GetHashCode()
    {
        return unchecked((int)Argb);
    }

    public static bool operator ==(RgbColor left, RgbColor right)
    {
        return left.Argb == right.Argb;
    }

    public static bool operator !=(RgbColor left, RgbColor right)
    {
        return left.Argb != right.Argb;
    }

    public override string ToString()
    {
        return IsEmpty ? "RgbColor [Empty]" : "RgbColor [" + Argb.ToString("X8", CultureInfo.InvariantCulture) + "]";
    }

    private static void CheckComponent(int value, string name)
    {
        if (value < 0 || value > 255)
        {
            throw new ArgumentOutOfRangeException(name, value, "A colour component must be in 0..255.");
        }
    }
}
