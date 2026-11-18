using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System.Drawing;

namespace SmarTTY.Controls;

// The terminal as the screen and the host of its escape sequence emulator (Terminal/Emulation/TerminalEmulator.cs): every member forwards to what the
// control already had. They are implemented explicitly, so that the public surface of the control is not changed.
public partial class TerminalEmulatorControl : ITerminalScreen, ITerminalHost
{
    GridPoint ITerminalScreen.Cursor
    {
        get => CursorPosition.ToGridPoint();
        set => CursorPosition = value.ToPoint();
    }

    int ITerminalScreen.Rows => _visibleRows;

    int ITerminalScreen.Columns => _visibleColumns;

    TabStops ITerminalScreen.Tabs => Tabs;

    CellStyle ITerminalScreen.CurrentStyle
    {
        get => CurrentStyle;
        set => CurrentStyle = value;
    }

    int ITerminalScreen.ActiveBufferIndex
    {
        get => ActiveBufferIndex;
        set => ActiveBufferIndex = value;
    }

    bool ITerminalScreen.HasScrollRegion => HasScrollRegion;

    int ITerminalScreen.ScrollTop => ScrollTop;

    int ITerminalScreen.ScrollBottom => ScrollBottom;

    bool ITerminalScreen.InsertMode
    {
        set => _insertMode = value;
    }

    bool ITerminalScreen.HideCaret
    {
        set => HideCaret = value;
    }

    void ITerminalScreen.MoveCursorBy(int deltaX, int deltaY, bool scroll)
    {
        MoveCursorBy(deltaX, deltaY, scroll);
    }

    void ITerminalScreen.InsertChars(int count)
    {
        InsertChars(count);
    }

    void ITerminalScreen.DeleteChars(int count)
    {
        DeleteChars(count);
    }

    void ITerminalScreen.EraseInDisplay(EraseMode eraseMode)
    {
        EraseInDisplay(eraseMode);
    }

    void ITerminalScreen.EraseInLine(EraseMode eraseMode)
    {
        EraseInLine(eraseMode);
    }

    void ITerminalScreen.InsertBlankLines(int line, int count)
    {
        InsertBlankLines(line, count);
    }

    void ITerminalScreen.ScrollRegionUp(int topLine, int count)
    {
        ScrollRegionUp(topLine, count);
    }

    void ITerminalScreen.WriteAt(GridPoint point, string text, CellStyle cellStyle)
    {
        WriteAt(point.ToPoint(), text, cellStyle);
    }

    void ITerminalScreen.RepeatPreviousChar(int count)
    {
        RepeatPreviousChar(count);
    }

    void ITerminalScreen.SetScrollRegion(int top, int bottom)
    {
        SetScrollRegion(top, bottom);
    }

    void ITerminalScreen.ClearScrollRegion()
    {
        ClearScrollRegion();
    }

    void ITerminalScreen.ClearActiveScreen()
    {
        ClearActiveScreen();
    }

    void ITerminalScreen.InvalidateView()
    {
        InvalidateView();
    }

    void ITerminalScreen.BeginBatch()
    {
        BeginBatch();
    }

    void ITerminalScreen.EndBatch()
    {
        EndBatch();
    }

    void ITerminalScreen.ResetLinkTracking()
    {
        ResetLinkTracking();
    }

    void ITerminalScreen.WriteText(string text)
    {
        WriteText(text);
    }

    void ITerminalHost.SendReply(string reply)
    {
        if (!SuppressReplies)
        {
            SendText(reply, InputKind.KeySequence);
        }
    }

    int ITerminalHost.TabWidth => _tabWidth;

    bool ITerminalHost.ByteColumnsActive => ByteColumnsActive;

    int ITerminalHost.ByteOffset
    {
        set => _byteOffset = value;
    }

    void ITerminalHost.ResetByteState()
    {
        ResetByteState();
    }

    void ITerminalHost.ResetFromStream()
    {
        ResetFromStream();
    }

    void ITerminalHost.MoveForwardBytes(int bytes)
    {
        MoveForwardBytes(bytes);
    }

    int ITerminalHost.MoveBackBytes(int bytes)
    {
        return MoveBackBytes(bytes);
    }

    int ITerminalHost.MaxChunkLength => MaxChunkLength;

    bool ITerminalHost.ShowUnrecognizedAsHex => ShowUnrecognizedAsHex;

    void ITerminalHost.WriteOutputText(string text)
    {
        WriteOutputText(text);
    }

    void ITerminalHost.Bell()
    {
        OnBell();
    }

    void ITerminalHost.Trace(string text)
    {
        Trace(text);
    }

    IEscapeTrace ITerminalHost.BeginEscapeTrace()
    {
#if DEBUG
        // Escape diagnostics exist in a Debug build only. The tracing object is created just around the sequence, so its Dispose()
        // reports what the sequence meant right after it is parsed. A Release build compiles the whole thing out.
        return new EscapeTrace(this);
#else
        return null;
#endif
    }

    MouseReportingMode ITerminalHost.MouseMode
    {
        set => MouseMode = value;
    }

    string ITerminalHost.Title
    {
        set => Title = value;
    }

    string ITerminalHost.ReportedDirectory
    {
        set => ReportedDirectory = value;
    }
}
