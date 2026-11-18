using System;
using SmarTTY.TextEditing;
using VisualTTY;

namespace SmarTTY.Terminal;

public class CompletionPopupRequest : EventArgs
{
    /// <summary>The place of the popup on the screen, in pixels (<c>X</c> to the right, <c>Y</c> down): the caret of the command line.</summary>
    public GridPoint ScreenLocation;

    public CompletionItem[] Items;

    public int LineHeight;

    public string Title;

    public string StatusText;

    public ICompletionPopup Popup;

    public VirtualTerminalPopupIcon Icon;
}
