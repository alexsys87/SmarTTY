using SmarTTY.TextEditing;

namespace SmarTTY.Controls;

// The WinForms terminal as the terminal of a tab (ITerminalSurface): the members it does not have under the same name, implemented
// explicitly, so that the public surface of the control is not changed. The Smart Terminal still uses this control until it moves to WPF.
public partial class TerminalEmulatorControl : ITerminalSurface
{
    GridPoint ITerminalSurface.GridSize => new GridPoint(TerminalSize.Width, TerminalSize.Height);

    GridPoint ITerminalSurface.CursorCell => CursorPosition.ToGridPoint();

    string ITerminalSurface.GetScreenLineText(int row)
    {
        return GetLineText(row);
    }

    void ITerminalSurface.FocusTerminal()
    {
        Focus();
    }

    ISearchTarget ITerminalSurface.SearchTarget => this;
}
