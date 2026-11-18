using SmarTTY.CodeEditing;
using SmarTTY.TextEditing;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace SmarTTY.Controls;

/// <summary>
/// Tells the text view which lines the folded blocks hide and how a folded header line looks (the folded text, with its
/// label in gray inside a gray frame). The blocks themselves are the <see cref="FoldingManager"/>'s.
/// </summary>
internal class FoldingLineProvider : ReadonlyTextBrowser.IVirtualLineProvider
{
    private readonly FoldingManager _manager;

    // The view keeps asking for the decorator of a line; it is one object for as long as the block stays folded.
    private readonly ConditionalWeakTable<FoldingManager.FoldedRegion, RegionDecorator> _decorators = new ConditionalWeakTable<FoldingManager.FoldedRegion, RegionDecorator>();

    public FoldingLineProvider(FoldingManager foldingManager)
    {
        _manager = foldingManager;
    }

    private class RegionDecorator : ReadonlyTextBrowser.ILineDecorator
    {
        private readonly FoldingManager.FoldedRegion _region;

        public RegionDecorator(FoldingManager.FoldedRegion region)
        {
            _region = region;
        }

        public string Transform(string lineText, out CharacterRange characterRange, out Brush brush)
        {
            string text = _region.Transform(lineText, out int labelStart, out int labelLength);
            characterRange = new CharacterRange(labelStart, labelLength);
            brush = Brushes.DarkGray;
            return text;
        }

        public void DrawDecoration(Graphics graphics, SizeF sizeF, Rectangle rectangle, CharacterRange characterRange)
        {
            graphics.DrawRectangle(Pens.DarkGray, rectangle);
        }

        public TextDocument.TextPosition MapPosition(string text, TextDocument.TextPosition textPosition)
        {
            return _region.MapPosition(text, textPosition);
        }
    }

    public int GetTotalVirtualRows()
    {
        return 0;
    }

    public int GetTotalHiddenLines()
    {
        return _manager.GetTotalHiddenLines();
    }

    public int GetNextSpecialLine(int lineIndex)
    {
        return _manager.GetNextSpecialLine(lineIndex);
    }

    public int GetVirtualRowCount(int lineIndex)
    {
        return 0;
    }

    public void GetVirtualLines(int lineIndex, List<ReadonlyTextBrowser.IVirtualLine> list)
    {
    }

    public int GetHiddenLineCount(int lineIndex)
    {
        return _manager.GetHiddenLineCount(lineIndex);
    }

    public ReadonlyTextBrowser.ILineDecorator GetDecorator(int lineIndex)
    {
        FoldingManager.FoldedRegion region = _manager.GetRegion(lineIndex);
        if (region == null)
        {
            return null;
        }
        return _decorators.GetValue(region, (FoldingManager.FoldedRegion folded) => new RegionDecorator(folded));
    }

    public bool OnCaretMoved(int lineIndex, int columnIndex)
    {
        return _manager.OnCaretMoved(lineIndex, columnIndex);
    }
}
