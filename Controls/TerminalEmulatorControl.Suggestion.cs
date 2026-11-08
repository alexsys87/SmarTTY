using SmarTTY.Terminal;
using System.Drawing;

namespace SmarTTY.Controls;

// The grey suggestion from the history drawn after the typed text (see Terminal/HistorySuggestion.cs), shared by the
// Smart Terminal and the COM/TCP terminal: only the drawing is here, each of them decides what to suggest and when.
public partial class TerminalEmulatorControl
{
    /// <summary>
    /// Draws <paramref name="suggestion"/> from <paramref name="column"/> (counted from the left edge of the text area) on
    /// the screen row at <paramref name="top"/>, cut at the right edge of the window. Called from PaintLineOverlay.
    /// </summary>
    protected void PaintHistorySuggestion(Graphics graphics, string suggestion, int column, int left, int top)
    {
        string visible = HistorySuggestion.FitToWidth(suggestion, column, _visibleColumns);
        if (visible.Length == 0)
        {
            return;
        }
        // Halfway between the text and the background, so it reads as a hint in light and dark colour schemes alike.
        Color fore = (_defaultColors[0].Foreground as SolidBrush)?.Color ?? ForeColor;
        Color back = BackColor;
        using SolidBrush brush = new SolidBrush(Color.FromArgb((fore.R + back.R) / 2, (fore.G + back.G) / 2, (fore.B + back.B) / 2));
        float charWidth = CharSize.Width;
        // One character per cell, as PaintLine places the typed text, so the suggestion stays on the same grid.
        for (int i = 0; i < visible.Length; i++)
        {
            if (visible[i] != ' ')
            {
                graphics.DrawString(visible.Substring(i, 1), Font, brush, left + (column + i) * charWidth, top, _stringFormat);
            }
        }
    }
}
