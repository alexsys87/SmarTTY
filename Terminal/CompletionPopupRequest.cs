using System;
using System.Drawing;
using VisualTTY;

namespace SmarTTY.Terminal;

public class CompletionPopupRequest : EventArgs
{
    public Point ScreenLocation;

    public CompletionItem[] Items;

    public int LineHeight;

    public string Title;

    public string StatusText;

    public ICompletionPopup Popup;

    public VirtualTerminalPopupIcon Icon;
}
