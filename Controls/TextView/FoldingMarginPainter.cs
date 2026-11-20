using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System.Drawing;

namespace SmarTTY.Controls;

/// <summary>
/// The margin of the text view that shows the fold marks and toggles a fold on a click. What each line shows comes from the
/// <see cref="FoldingManager"/>; this class draws it.
/// </summary>
internal class FoldingMarginPainter : IMargin
{
    private FoldingManager _manager;

    public FoldingMarginPainter(FoldingManager foldingManager)
    {
        _manager = foldingManager;
    }

    public void Paint(Graphics graphics, Rectangle rectangle, SizeF charSize, int firstLineIndex, int lastLineIndex, int rowCount, ReadonlyTextBrowser readonlyTextBrowser)
    {
        float yPos = rectangle.Top;
        // The text colour: black lines would not be seen on a dark editor.
        using Pen pen = new Pen(Color.FromArgb(170, readonlyTextBrowser.ForeColor));
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            int lineIndex = readonlyTextBrowser.DisplayRows[rowIdx].Line;
            FoldingManager.GutterState gutterState = FoldingManager.GutterState.Plain;
            // Folding is not available while long lines are wrapped (see ReadonlyTextBrowser.WordWrap).
            if (readonlyTextBrowser.DisplayRows[rowIdx].VirtualLine == null && lineIndex != -1 && !readonlyTextBrowser.IsWrapping)
            {
                gutterState = this._manager.GetGutterState(lineIndex);
            }
            int yOffset = ((int)charSize.Height - 10) / 2;
            int xCenter = rectangle.Left + 1 + 5;
            switch (gutterState)
            {
                case FoldingManager.GutterState.Plain:
                case FoldingManager.GutterState.SectionEnd:
                    graphics.DrawLine(pen, xCenter, yPos, xCenter, yPos + charSize.Height);
                    if (gutterState == FoldingManager.GutterState.SectionEnd)
                    {
                        graphics.DrawLine(pen, xCenter, yPos + charSize.Height / 2f, rectangle.Right, yPos + charSize.Height / 2f);
                    }
                    break;
                case FoldingManager.GutterState.Expandable:
                case FoldingManager.GutterState.Collapsed:
                    graphics.DrawLine(pen, xCenter, yPos, xCenter, yPos + (float)yOffset);
                    graphics.DrawRectangle(pen, xCenter - 5, yPos + (float)yOffset, 10f, 10f);
                    graphics.DrawLine(pen, xCenter, yPos + (float)yOffset + 10f, xCenter, yPos + charSize.Height);
                    graphics.DrawLine(pen, xCenter - 3, yPos + (float)yOffset + 5f, xCenter + 3, yPos + (float)yOffset + 5f);
                    if (gutterState == FoldingManager.GutterState.Collapsed)
                    {
                        graphics.DrawLine(pen, xCenter, yPos + (float)yOffset + 2f, xCenter, yPos + (float)yOffset + 10f - 2f);
                    }
                    break;
            }
            yPos += charSize.Height;
        }
    }

    public int MeasureWidth(ReadonlyTextBrowser readonlyTextBrowser, Graphics graphics, int lineCount)
    {
        return 12;
    }

    public bool OnMouseDown(Point point, ReadonlyTextBrowser readonlyTextBrowser, int firstLineIndex, int lastLineIndex, SizeF charSize, int rowIdx)
    {
        if (readonlyTextBrowser.DisplayRows[rowIdx].VirtualLine == null && this._manager.ClickMark(readonlyTextBrowser.DisplayRows[rowIdx].Line, readonlyTextBrowser.IsWrapping, readonlyTextBrowser.CaretPosition, out TextDocument.TextPosition? caretTarget))
        {
            if (caretTarget.HasValue)
            {
                readonlyTextBrowser.SetCaret(caretTarget.Value);
            }
            readonlyTextBrowser.RebuildDisplayRows();
            readonlyTextBrowser.UpdateLayout();
            readonlyTextBrowser.Redraw();
        }
        return true;
    }
}
