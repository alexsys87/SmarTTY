using System;
using System.Drawing;
using SmarTTY.TextEditing;
using System.Windows.Forms;

namespace SmarTTY.Controls;

// Selecting with the mouse past the edge of the text scrolls it, as in any editor.
public partial class ReadonlyTextBrowser
{
    private const int DragScrollIntervalMs = 50;

    private Timer _dragScrollTimer;

    private void StartDragScroll()
    {
        if (_terminalMode)
        {
            return;
        }
        if (_dragScrollTimer == null)
        {
            _dragScrollTimer = new Timer { Interval = DragScrollIntervalMs };
            _dragScrollTimer.Tick += (sender, e) => DragScrollStep();
            Disposed += (sender, e) => _dragScrollTimer.Dispose();
        }
        _dragScrollTimer.Start();
    }

    private void StopDragScroll()
    {
        _dragScrollTimer?.Stop();
    }

    private void DragScrollStep()
    {
        if ((Control.MouseButtons & MouseButtons.Left) == 0 || _terminalMode || _navigationLocked)
        {
            StopDragScroll();
            return;
        }
        Point mouse = PointToClient(Cursor.Position);
        int rowHeight = Math.Max(1, (int)_charSize.Height);
        int columnWidth = Math.Max(1, (int)_charSize.Width);
        int overY = mouse.Y < _textArea.Top ? mouse.Y - _textArea.Top : (mouse.Y >= _textArea.Bottom ? mouse.Y - _textArea.Bottom + 1 : 0);
        int overX = mouse.X < _textArea.Left ? mouse.X - _textArea.Left : (mouse.X >= _textArea.Right ? mouse.X - _textArea.Right + 1 : 0);
        if (IsWrapping || _noHorizontalScroll)
        {
            overX = 0;
        }
        if (overX == 0 && overY == 0)
        {
            return;
        }
        // The farther the mouse is from the text, the faster it scrolls.
        if (overY != 0)
        {
            int rows = 1 + Math.Abs(overY) / rowHeight;
            ScrollLimits limits = _verticalScrollBar.Limits;
            int maxTop = NativeScrollSemantics ? Math.Max(_verticalScrollBar.Maximum - limits.LargeChange + 1, 0) : Math.Max(_verticalScrollBar.Maximum, 0);
            int newTop = Math.Max(limits.Minimum, Math.Min(ScrollLine + Math.Sign(overY) * rows, maxTop));
            if (newTop != ScrollLine)
            {
                ScrollLine = newTop;
            }
        }
        if (overX != 0)
        {
            int columns = 1 + Math.Abs(overX) / columnWidth;
            ScrollColumn = Math.Max(0, ScrollColumn + Math.Sign(overX) * columns);
        }
        RebuildDisplayRows();
        // The selection ends at the edge of the text that the mouse went past.
        int x = Math.Max(_textArea.Left, Math.Min(mouse.X, _textArea.Right - 1));
        int y = Math.Max(_textArea.Top, Math.Min(mouse.Y, _textArea.Bottom - 1));
        MoveCaretToPoint(new Point(x, y));
        UpdateCaret();
        Redraw();
    }
}
