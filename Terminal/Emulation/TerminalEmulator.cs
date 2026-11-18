using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// What the escape sequences of a program do: the CSI commands (the cursor, erasing, scrolling, the modes, the colours, the replies),
/// the OSC strings (title, directory), the saving of the cursor. It works on the screen through <see cref="ITerminalScreen"/> and on the
/// control around it through <see cref="ITerminalHost"/>; the state the sequences set is in <see cref="TerminalModes"/>.
/// </summary>
internal sealed class TerminalEmulator
{
    private readonly ITerminalScreen _screen;

    private readonly ITerminalHost _host;

    private readonly TerminalModes _modes;

    public TerminalEmulator(ITerminalScreen screen, ITerminalHost host, TerminalModes modes)
    {
        _screen = screen;
        _host = host;
        _modes = modes;
    }

    // The numbers come from the output of the remote side, negative ones included: see PrivateModeTable.
    private PrivateModeTable _privateModes => _modes.PrivateModes;

    /// <summary>ESC 7, CSI s and CSI ? 1048 h: remembers the cursor and the style of the text.</summary>
    public void SaveCursor()
    {
        _modes.SavedCursor = _screen.Cursor;
        _modes.SavedStyle = _screen.CurrentStyle;
    }

    /// <summary>ESC 8, CSI u and CSI ? 1048 l: back to the remembered cursor and style.</summary>
    public void RestoreCursor()
    {
        _screen.Cursor = _modes.SavedCursor;
        _screen.CurrentStyle = _modes.SavedStyle;
    }

    public bool ExecuteCsi(char ch, string[] items, bool set, IEscapeTrace escapeTrace = null)
    {
        if ("CDJKmXP@hlnc".IndexOf(ch) == -1)
        {
            // Every other command that moves the cursor puts it on a cell boundary (byte mode).
            _host.ByteOffset = 0;
        }
        escapeTrace?.SetRaw(string.Format("Unknown CSI command: {2}{0}, DEC={1}", ch, set, string.Join(";", items)));
        int count = 0;
        int repeatCount = 1;
        if (items.Length != 0 && int.TryParse(items[0], out count) && count > 0)
        {
            // A count of 0 means 1, as an omitted one does.
            repeatCount = count;
        }
        // The count is the remote side's to choose, up to int.MaxValue: it is cut to what the screen can use (see CsiRepeatLimit).
        repeatCount = CsiRepeatLimit.Clamp(ch, repeatCount, _screen.Rows, _screen.Columns);
        switch (ch)
        {
            case '@':
                _screen.InsertChars(repeatCount);
                escapeTrace?.SetDescription($"Insert char at {repeatCount}");
                return true;
            case 'A':
                escapeTrace?.SetDescription($"Go up {repeatCount}");
                _screen.MoveCursorBy(0, -repeatCount);
                return true;
            case 'B':
                escapeTrace?.SetDescription($"Go down {repeatCount}");
                _screen.MoveCursorBy(0, repeatCount);
                return true;
            case 'C':
                escapeTrace?.SetDescription($"Go right {repeatCount}");
                if (_host.ByteColumnsActive)
                {
                    _host.MoveForwardBytes(repeatCount);
                }
                else
                {
                    _screen.MoveCursorBy(repeatCount, 0);
                }
                return true;
            case 'D':
                escapeTrace?.SetDescription($"Go left {repeatCount}");
                if (_host.ByteColumnsActive)
                {
                    _host.MoveBackBytes(repeatCount);
                }
                else
                {
                    _screen.MoveCursorBy(-repeatCount, 0);
                }
                return true;
            case 'E':
                escapeTrace?.SetDescription($"Go to start of relative line {repeatCount}");
                _screen.MoveCursorBy(-_screen.Cursor.X, repeatCount);
                return true;
            case 'F':
                escapeTrace?.SetDescription($"Go to start of relative line -{repeatCount}");
                _screen.MoveCursorBy(-_screen.Cursor.X, -repeatCount);
                return true;
            case 'G':
            case '`':
                escapeTrace?.SetDescription($"Set X= {repeatCount}");
                _screen.Cursor = new GridPoint(repeatCount - 1, _screen.Cursor.Y);
                return true;
            case 'H':
            case 'f':
                {
                    int[] coordinates = CsiParameters.Parse(items, 1, 1);
                    escapeTrace?.SetDescription($"GOTOXY {coordinates[1]}, {coordinates[0]}");
                    _screen.Cursor = new GridPoint(coordinates[1] - 1, coordinates[0] - 1);
                    return true;
                }
            case 'd':
                escapeTrace?.SetDescription($"Set Y= {repeatCount}");
                _screen.Cursor = new GridPoint(_screen.Cursor.X, repeatCount - 1);
                return true;
            case 'I':
                {
                    escapeTrace?.SetDescription($"Go {repeatCount} tabs");
                    for (int tabIndex = 0; tabIndex < repeatCount; tabIndex++)
                    {
                        _screen.MoveCursorBy(_screen.Tabs.NextDistance(_screen.Cursor.X, _host.TabWidth), 0);
                    }
                    return true;
                }
            case 'J':
                escapeTrace?.SetDescription($"Erase terminal {(EraseMode)count}");
                if ((uint)count <= 2u)
                {
                    _screen.EraseInDisplay((EraseMode)count);
                }
                _screen.InvalidateView();
                return true;
            case 'K':
                escapeTrace?.SetDescription($"Erase line {(EraseMode)count}");
                if ((uint)count <= 2u)
                {
                    _screen.EraseInLine((EraseMode)count);
                }
                _screen.InvalidateView();
                return true;
            case 'L':
                escapeTrace?.SetDescription($"Insert {repeatCount} lines");
                _screen.InsertBlankLines(_screen.Cursor.Y, repeatCount);
                return true;
            case 'M':
                escapeTrace?.SetDescription($"Remove {repeatCount} lines");
                _screen.ScrollRegionUp(_screen.Cursor.Y, repeatCount);
                return true;
            case 'P':
                escapeTrace?.SetDescription($"Erase {repeatCount} chars");
                _screen.DeleteChars(repeatCount);
                return true;
            case 'S':
                escapeTrace?.SetDescription($"Remove {repeatCount} lines from clip region top");
                _screen.ScrollRegionUp(_screen.ScrollTop, repeatCount);
                return true;
            case 'T':
                escapeTrace?.SetDescription($"Insert {repeatCount} lines to clip region top");
                _screen.InsertBlankLines(_screen.ScrollTop, repeatCount);
                return true;
            case 'X':
                {
                    // ECH erases up to the end of the line, it never wraps onto the next one.
                    int eraseCount = Math.Min(repeatCount, _screen.Columns - _screen.Cursor.X);
                    escapeTrace?.SetDescription($"Pad {eraseCount} chars");
                    if (eraseCount > 0)
                    {
                        _screen.WriteAt(_screen.Cursor, new string(' ', eraseCount), _screen.CurrentStyle);
                    }
                    return true;
                }
            case 'Z':
                {
                    escapeTrace?.SetDescription($"Go {repeatCount} tabs back");
                    for (int backTabIndex = 0; backTabIndex < repeatCount; backTabIndex++)
                    {
                        _screen.MoveCursorBy(-_screen.Tabs.PreviousDistance(_screen.Cursor.X), 0);
                    }
                    return true;
                }
            case 'b':
                escapeTrace?.SetDescription("Clone char");
                _screen.RepeatPreviousChar(repeatCount);
                return true;
            case 'g':
                escapeTrace?.SetDescription("Clear/reset tab");
                switch (count)
                {
                    case 0:
                        _screen.Tabs.ClearStop(_screen.Cursor.X);
                        break;
                    case 3:
                        _screen.Tabs.ClearAll();
                        break;
                }
                return true;
            case 'm':
                if (!set)
                {
                    CellStyle style = _screen.CurrentStyle;
                    SgrStyle.Apply(ref style, XtermProtocol.ParseSgr(items));
                    _screen.CurrentStyle = style;
                    escapeTrace?.SetDescription($"SGR: {style}");
                }
                return true;
            case 'n':
                if (!set)
                {
                    ReportStatus(count, escapeTrace);
                }
                return true;
            case 'c':
                if (!set && count == 0)
                {
                    // Primary device attributes: a VT100 with advanced video option, as xterm reports by default.
                    escapeTrace?.SetDescription("Device attributes");
                    _host.SendReply("\u001b[?1;2c");
                }
                return true;
            case 'u':
                if (!set)
                {
                    escapeTrace?.SetDescription("Restore cursor (SCORC)");
                    RestoreCursor();
                }
                return true;
            case 'h':
            case 'l':
                {
                    string[] sgrParameters = items;
                    for (int sgrParameterIndex = 0; sgrParameterIndex < sgrParameters.Length; sgrParameterIndex++)
                    {
                        if (int.TryParse(sgrParameters[sgrParameterIndex], out var mode))
                        {
                            SetMode(mode, set, ch == 'h', escapeTrace);
                            if (set)
                            {
                                _privateModes.Set(mode, ch == 'h');
                            }
                        }
                    }
                    return true;
                }
            case 's':
                escapeTrace?.SetDescription("Save features: " + string.Join(";", items));
                if (!set)
                {
                    // SCOSC: the ANSI.SYS form of "save cursor".
                    SaveCursor();
                }
                else
                {
                    string[] modeParameters = items;
                    for (int modeParameterIndex = 0; modeParameterIndex < modeParameters.Length; modeParameterIndex++)
                    {
                        if (int.TryParse(modeParameters[modeParameterIndex], out var modeValue))
                        {
                            _privateModes.Save(modeValue);
                        }
                    }
                }
                return true;
            case 'r':
                if (set)
                {
                    escapeTrace?.SetDescription("Restore features: " + string.Join(";", items));
                    string[] savedModeParameters = items;
                    for (int savedModeParameterIndex = 0; savedModeParameterIndex < savedModeParameters.Length; savedModeParameterIndex++)
                    {
                        if (int.TryParse(savedModeParameters[savedModeParameterIndex], out var savedModeValue) && _privateModes.TryGetSaved(savedModeValue, out bool savedEnabled))
                        {
                            SetMode(savedModeValue, set: true, savedEnabled, escapeTrace);
                            _privateModes.Set(savedModeValue, savedEnabled);
                        }
                    }
                }
                else
                {
                    // DECSTBM: an omitted (or 0) top is the first line, an omitted bottom the last one.
                    int rows = _screen.Rows;
                    if (items.Length < 1 || !int.TryParse(items[0], out var top) || top < 1)
                    {
                        top = 1;
                    }
                    if (items.Length < 2 || !int.TryParse(items[1], out var bottom) || bottom < 1 || bottom > rows)
                    {
                        bottom = rows;
                    }
                    if (top == 1 && bottom == rows)
                    {
                        escapeTrace?.SetDescription("Reset clip region");
                        _screen.ClearScrollRegion();
                    }
                    else if (top < bottom)
                    {
                        escapeTrace?.SetDescription($"Set clip region to {top}..{bottom}");
                        _screen.SetScrollRegion(top - 1, bottom - 1);
                    }
                }
                return true;
            default:
                return false;
        }
    }

    private void SetMode(int mode, bool set, bool enabled, IEscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription($"Terminal feature: {_screen.CurrentStyle}");
        if (set)
        {
            switch (mode)
            {
                case 1:
                    _modes.ApplicationCursorKeys = enabled;
                    break;
                case 25:
                    _screen.HideCaret = !enabled;
                    break;
                case 47:
                    _screen.ActiveBufferIndex = (enabled ? 1 : 0);
                    break;
                case 1047:
                    // Alternate screen, cleared when it is left.
                    if (!enabled && _screen.ActiveBufferIndex == 1)
                    {
                        _screen.ClearActiveScreen();
                    }
                    _screen.ActiveBufferIndex = (enabled ? 1 : 0);
                    break;
                case 1048:
                    if (enabled)
                    {
                        SaveCursor();
                    }
                    else
                    {
                        RestoreCursor();
                    }
                    break;
                case 1049:
                    // Alternate screen, cleared when it is entered. The cursor starts where it was on the normal
                    // screen, and the normal screen gets its own cursor back when the program leaves (each buffer
                    // keeps its cursor, so an ESC 7 issued by the program cannot move it).
                    if (enabled)
                    {
                        GridPoint cursor = _screen.Cursor;
                        SaveCursor();
                        if (_screen.ActiveBufferIndex != 1)
                        {
                            _screen.ActiveBufferIndex = 1;
                        }
                        _screen.ClearActiveScreen();
                        _screen.Cursor = cursor;
                    }
                    else if (_screen.ActiveBufferIndex != 0)
                    {
                        _screen.ActiveBufferIndex = 0;
                    }
                    break;
                case 9:
                case 1000:
                case 1001:
                case 1002:
                case 1003:
                    if (enabled)
                    {
                        _host.MouseMode = (MouseReportingMode)mode;
                    }
                    else
                    {
                        _host.MouseMode = MouseReportingMode.Off;
                    }
                    break;
            }
        }
        else if (mode == 4)
        {
            _screen.InsertMode = enabled;
        }
    }

    public bool ExecuteOsc(string content, IEscapeTrace escapeTrace)
    {
        escapeTrace?.SetDescription("OSC");
        if (!OscString.TryParse(content, out int command, out string text))
        {
            return false;
        }
        if (command == 7)
        {
            // The directory of the shell (the prompt of bash and zsh sends it on many systems).
            string directory = TerminalDirectoryWatcher.UnixDirectoryOfOsc(command, text);
            if (directory != null)
            {
                _host.ReportedDirectory = directory;
            }
            return directory != null;
        }
        // 0 sets the icon name and the title, 2 the title only; 1 (the icon name alone) does not touch the title.
        if (command == 0 || command == 2)
        {
            _host.Title = OscString.TitleText(text);
            return true;
        }
        return false;
    }

    private void ReportStatus(int request, IEscapeTrace escapeTrace)
    {
        switch (request)
        {
            case 5:
                escapeTrace?.SetDescription("Status report");
                _host.SendReply("\u001b[0n");
                break;
            case 6:
                {
                    // Cursor position report; at the pending-wrap position the cursor is still on the last column.
                    escapeTrace?.SetDescription("Cursor position report");
                    GridPoint cursor = _screen.Cursor;
                    int column = Math.Min(cursor.X, Math.Max(_screen.Columns - 1, 0));
                    _host.SendReply($"\u001b[{cursor.Y + 1};{column + 1}R");
                    break;
                }
        }
    }
}
