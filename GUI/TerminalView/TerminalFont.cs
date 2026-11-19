using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SmarTTY.GUI.TerminalView;

/// <summary>
/// The font of the WPF terminal view as a grid: the glyph typeface of a monospaced font, the size of a cell (rounded to whole device pixels,
/// so that rows and columns never fall between pixels), the baseline, and the glyphs of the characters, from the font itself or, for a
/// character it does not have, from the first fallback font that has it. No text layout is done: a character takes the cell it is in.
/// </summary>
internal sealed class TerminalFont
{
    private static readonly string[] FallbackFamilies =
    {
        "Consolas", "Cascadia Mono", "Lucida Console", "Courier New", "Segoe UI Symbol", "Segoe UI Emoji", "MS Gothic", "SimSun",
        "Malgun Gothic", "Segoe UI", "Arial Unicode MS"
    };

    private readonly List<GlyphTypeface> _fallbacks = new List<GlyphTypeface>();

    private bool _fallbacksLoaded;

    // The face and glyph of a code point that the main face does not have (null face: no font has it).
    private readonly Dictionary<int, KeyValuePair<GlyphTypeface, ushort>> _fallbackGlyphs = new Dictionary<int, KeyValuePair<GlyphTypeface, ushort>>();

    public TerminalFont(FontFamily family, double emSize, double pixelsPerDip)
    {
        EmSize = emSize;
        PixelsPerDip = pixelsPerDip <= 0 ? 1 : pixelsPerDip;
        Typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!Typeface.TryGetGlyphTypeface(out GlyphTypeface face))
        {
            // A composite font (a family of the system that maps to several fonts) has no glyph typeface: the first fallback is used.
            face = LoadFace("Consolas") ?? LoadFace("Courier New");
            if (face == null)
            {
                throw new InvalidOperationException("No monospaced font is available for the terminal.");
            }
        }
        Face = face;
        double advance = face.AdvanceWidths.TryGetValue(GetMainGlyph('M'), out double width) ? width : 0.6;
        CellWidth = Snap(advance * emSize);
        CellHeight = Snap(face.Height * emSize);
        // The baseline in whole pixels too, so that every row puts its glyphs on the same pixel row.
        Baseline = Snap(face.Baseline * emSize);
        UnderlinePosition = Math.Min(CellHeight - OnePixel, Snap(Baseline - face.UnderlinePosition * emSize));
        UnderlineThickness = Math.Max(OnePixel, Snap(face.UnderlineThickness * emSize));
    }

    public Typeface Typeface { get; }

    /// <summary>The glyph typeface of the font.</summary>
    public GlyphTypeface Face { get; }

    public double EmSize { get; }

    /// <summary>The device pixels of one device-independent pixel (1.0 at 96 DPI, 2.0 at 192 DPI).</summary>
    public double PixelsPerDip { get; }

    /// <summary>The width of a cell, in device-independent pixels, a whole number of device pixels.</summary>
    public double CellWidth { get; }

    /// <summary>The height of a cell (the line height of the font), a whole number of device pixels.</summary>
    public double CellHeight { get; }

    /// <summary>The distance of the baseline from the top of a cell.</summary>
    public double Baseline { get; }

    /// <summary>The top of the underline from the top of a cell.</summary>
    public double UnderlinePosition { get; }

    public double UnderlineThickness { get; }

    /// <summary>One device pixel in device-independent pixels.</summary>
    public double OnePixel => 1.0 / PixelsPerDip;

    /// <summary>Rounds a length to whole device pixels (at least one).</summary>
    public double Snap(double value)
    {
        return Math.Max(1, Math.Round(value * PixelsPerDip)) / PixelsPerDip;
    }

    private ushort GetMainGlyph(int codePoint)
    {
        return Face.CharacterToGlyphMap.TryGetValue(codePoint, out ushort glyph) ? glyph : (ushort)0;
    }

    private static GlyphTypeface LoadFace(string familyName)
    {
        try
        {
            Typeface typeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return typeface.TryGetGlyphTypeface(out GlyphTypeface face) ? face : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The face and the glyph to draw a code point with. A character that no font has is drawn with the main face's glyph for '?'
    /// (the missing glyph of most fonts is an empty box that is hard to see on a dark background).
    /// </summary>
    public GlyphTypeface GetGlyph(int codePoint, out ushort glyph)
    {
        if (Face.CharacterToGlyphMap.TryGetValue(codePoint, out glyph))
        {
            return Face;
        }
        if (!_fallbackGlyphs.TryGetValue(codePoint, out KeyValuePair<GlyphTypeface, ushort> found))
        {
            found = FindFallback(codePoint);
            _fallbackGlyphs[codePoint] = found;
        }
        if (found.Key == null)
        {
            glyph = GetMainGlyph('?');
            return Face;
        }
        glyph = found.Value;
        return found.Key;
    }

    private KeyValuePair<GlyphTypeface, ushort> FindFallback(int codePoint)
    {
        if (!_fallbacksLoaded)
        {
            _fallbacksLoaded = true;
            foreach (string name in FallbackFamilies)
            {
                GlyphTypeface face = LoadFace(name);
                if (face != null && !ReferenceEquals(face, Face))
                {
                    _fallbacks.Add(face);
                }
            }
        }
        foreach (GlyphTypeface face in _fallbacks)
        {
            if (face.CharacterToGlyphMap.TryGetValue(codePoint, out ushort glyph))
            {
                return new KeyValuePair<GlyphTypeface, ushort>(face, glyph);
            }
        }
        return new KeyValuePair<GlyphTypeface, ushort>(null, 0);
    }
}
