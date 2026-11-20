using System;

namespace SmarTTY.CodeEditing;

/// <summary>
/// The zoom of the text of an editor (1 is the size of the font): the steps of Ctrl+wheel and the zoom commands, the rounding of a zoom that is
/// set and its limits. Moved as it was from <c>CodeEditorPP.ZoomBy</c>/<c>SetZoom</c> and the <c>FontZoom</c> setter of the WinForms text view
/// (stage 3E), so that the WPF text view zooms the same way.
/// </summary>
public static class EditorZoom
{
    /// <summary>A step of the zoom: a tenth of the size of the font.</summary>
    public const float Step = 0.1f;

    public const float Min = 0.5f;

    public const float Max = 4f;

    /// <summary>The zoom <paramref name="steps"/> steps from <paramref name="zoom"/>, on the tenths.</summary>
    public static float By(float zoom, int steps)
    {
        return (float)Math.Round(zoom / Step + steps) * Step;
    }

    /// <summary>A zoom that is set is rounded to hundredths.</summary>
    public static float Round(float zoom)
    {
        return (float)Math.Round(zoom, 2);
    }

    /// <summary>A zoom brought between <see cref="Min"/> and <see cref="Max"/>.</summary>
    public static float Clamp(float zoom)
    {
        return Math.Max(Min, Math.Min(Max, zoom));
    }
}
