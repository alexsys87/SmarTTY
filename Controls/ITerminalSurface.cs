using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using System;

namespace SmarTTY.Controls;

/// <summary>
/// The terminal of a tab as the window, the session and the menus use it, whatever draws it: the WPF terminal of stage 2
/// (<c>GUI/TerminalView/WpfTerminal</c> and its subclasses; the WinForms terminal was one too until stage 2).
/// Every member is one that the code outside the control called on it. Most calls come from the UI thread; these come from other threads
/// too and must not touch anything that belongs to the UI thread (a WPF dependency property throws there): <see cref="Tag"/>,
/// <see cref="IsDisposed"/> and <see cref="ByteOrientedLineEditing"/> (the reading thread of the channel and the thread of the input
/// dispatcher), <see cref="EnqueueOutput"/> and <see cref="WriteLog"/> (the reading thread of the channel).
/// </summary>
public interface ITerminalSurface
{
    /// <summary>What the owner keeps with the terminal (the channel and the session of a tab, <c>TerminalTag</c>).</summary>
    object Tag { get; set; }

    bool IsDisposed { get; }

    void Dispose();

    /// <summary>The title the program set (OSC 0/2).</summary>
    string Title { get; }

    event EventHandler TitleChanged;

    /// <summary>The size of the screen in cells: X columns, Y rows.</summary>
    GridPoint GridSize { get; }

    /// <summary>The cursor on the screen (X column, Y row).</summary>
    GridPoint CursorCell { get; }

    /// <summary>The text of a row of the screen (0 is its top row).</summary>
    string GetScreenLineText(int row);

    /// <summary>The directory the shell reported with OSC 7, or null.</summary>
    string ReportedDirectory { get; }

    /// <summary>The remote line editor counts UTF-8 bytes (see <c>TerminalDisplayModel.ByteOrientedLineEditing</c>).</summary>
    bool ByteOrientedLineEditing { get; set; }

    /// <summary>No answers to the queries of the program are sent (a replayed log).</summary>
    bool SuppressReplies { get; set; }

    bool HideCaret { get; set; }

    bool AutoWrap { get; set; }

    /// <summary>Raised with every chunk of output as it arrives, before it is drawn.</summary>
    event Action<string> OutputReceived;

    /// <summary>Queues output of the remote side (any thread).</summary>
    void EnqueueOutput(string text);

    /// <summary>Parses output at once (UI thread): messages of SmarTTY itself, a replayed log.</summary>
    void ProcessOutput(string output);

    /// <summary>Opens (or, with null, closes) the file the raw output is copied to.</summary>
    void OpenLogFile(string path);

    /// <summary>Copies raw output to the log file, if one is open (any thread).</summary>
    void WriteLog(byte[] buffer, int offset, int count);

    /// <summary>Sends text as if the user had typed it.</summary>
    void TypeText(string text);

    /// <summary>Puts a command into the command line without running it.</summary>
    void InsertCommand(string text);

    void Paste();

    void PasteText(string text);

    void PasteSpecial(PasteMode mode);

    void CopySelection(bool select = true);

    void CopyAll();

    /// <summary>"Clear": the scrollback and the screen above the line of the cursor.</summary>
    void ClearOutput();

    /// <summary>A new session starts in the terminal (a reconnect).</summary>
    void BeginNewSession();

    /// <summary>Gives the terminal the keyboard focus.</summary>
    void FocusTerminal();

    /// <summary>What the search bar of the window works on.</summary>
    ISearchTarget SearchTarget { get; }
}
