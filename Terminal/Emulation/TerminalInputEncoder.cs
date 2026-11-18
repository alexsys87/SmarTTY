using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Emulation;

/// <summary>The mouse button of an event; <see cref="None"/> is a motion with no button held, <see cref="Other"/> a button that the terminal does not report.</summary>
public enum TerminalMouseButton
{
    None,
    Left,
    Middle,
    Right,
    Other
}

/// <summary>
/// What the terminal sends to the program for the mouse: whether an event is reported in the mode the program asked for, and the bytes of the report. Each
/// method returns the bytes to send, or null when the event is not reported.
/// </summary>
public sealed class TerminalInputEncoder
{
    // The cell of the last motion that was reported (motion is reported when the cell changes, not for every pixel).
    private GridPoint _lastMouseCell = new GridPoint(-1, -1);

    /// <summary>The mouse mode changed: the next motion is reported whatever the cell.</summary>
    public void ResetMouseMotion()
    {
        _lastMouseCell = new GridPoint(-1, -1);
    }

    /// <summary>A button was pressed or released. <paramref name="modifierBits"/> are the Shift (4), Alt (8) and Control (16) bits of the keys held.</summary>
    public byte[] MouseButton(MouseReportingMode mode, TerminalMouseButton button, bool pressed, int modifierBits, GridPoint cell, int columns, int rows, bool sgr)
    {
        int buttonCode;
        switch (button)
        {
            case TerminalMouseButton.Left:
                buttonCode = 0;
                break;
            case TerminalMouseButton.Middle:
                buttonCode = 1;
                break;
            case TerminalMouseButton.Right:
                buttonCode = 2;
                break;
            default:
                return null;
        }
        switch (mode)
        {
            case MouseReportingMode.X10:
                if (!pressed)
                {
                    return null;
                }
                break;
            case MouseReportingMode.Normal:
            case MouseReportingMode.Highlight:
            case MouseReportingMode.ButtonEvent:
            case MouseReportingMode.AnyEvent:
                buttonCode |= modifierBits;
                break;
            default:
                return null;
        }
        return Report(buttonCode, cell, release: !pressed, columns, rows, sgr);
    }

    /// <summary>The wheel was turned (<paramref name="down"/>: away from the user).</summary>
    public byte[] MouseWheel(MouseReportingMode mode, bool down, int modifierBits, GridPoint cell, int columns, int rows, bool sgr)
    {
        int wheelCode = 64 + (down ? 1 : 0);
        if (mode != MouseReportingMode.X10 && (uint)(mode - 1000) <= 3u)
        {
            wheelCode |= modifierBits;
            return Report(wheelCode, cell, release: false, columns, rows, sgr);
        }
        return null;
    }

    /// <summary>The mouse moved to a cell (with a button held, or with <see cref="TerminalMouseButton.None"/>).</summary>
    public byte[] MouseMove(MouseReportingMode mode, TerminalMouseButton button, int modifierBits, GridPoint cell, int columns, int rows, bool sgr)
    {
        // Motion reports carry the held button, or 3 ("no button") for plain motion in any-event mode.
        int buttonCode;
        switch (button)
        {
            case TerminalMouseButton.Left:
                buttonCode = 0;
                break;
            case TerminalMouseButton.Middle:
                buttonCode = 1;
                break;
            case TerminalMouseButton.Right:
                buttonCode = 2;
                break;
            default:
                buttonCode = 3;
                break;
        }
        switch (mode)
        {
            case MouseReportingMode.ButtonEvent:
                if (buttonCode == 3)
                {
                    return null;
                }
                break;
            case MouseReportingMode.AnyEvent:
                break;
            default:
                return null;
        }
        if (cell.Equals(_lastMouseCell))
        {
            return null;
        }
        _lastMouseCell = cell;
        return Report(buttonCode | 32 | modifierBits, cell, release: false, columns, rows, sgr);
    }

    private static byte[] Report(int buttonCode, GridPoint cell, bool release, int columns, int rows, bool sgr)
    {
        // A click in scrolled-back text or in the partial cell at the edge is reported at the nearest screen cell.
        int column = Math.Max(0, Math.Min(cell.X, columns - 1));
        int row = Math.Max(0, Math.Min(cell.Y, rows - 1));
        return XtermProtocol.EncodeMouseReport(buttonCode, column, row, release, sgr);
    }
}
