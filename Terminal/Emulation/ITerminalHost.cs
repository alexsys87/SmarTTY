namespace SmarTTY.Terminal.Emulation;

/// <summary>
/// What the parser of the escape sequences needs of the control around it, besides the screen: the replies to the program, the byte
/// oriented line editing of a serial terminal, and the things a program can set that belong to the window (the mouse mode, the title,
/// the directory of the shell). Every member is one that the parser used on the control before.
/// </summary>
public interface ITerminalHost
{
    /// <summary>Sends a reply to the program as if it had been typed (unless replies are suppressed).</summary>
    void SendReply(string reply);

    /// <summary>The number of columns of a tab (a setting of the terminal).</summary>
    int TabWidth { get; }

    /// <summary>Byte mode: the cursor moves by the bytes of a character, not by cells.</summary>
    bool ByteColumnsActive { get; }

    /// <summary>Byte mode: how many bytes into the character at the cursor the remote cursor is.</summary>
    int ByteOffset { set; }

    /// <summary>Byte mode: forgets the partial character and the offset (a reset).</summary>
    void ResetByteState();

    /// <summary>ESC c: a full reset of the terminal that the remote side asked for (the output queued after it is still valid).</summary>
    void ResetFromStream();

    void MoveForwardBytes(int bytes);

    /// <summary>Moves the cursor back by bytes; returns the bytes that were left at the start of the line.</summary>
    int MoveBackBytes(int bytes);

    /// <summary>The longest chunk of output that is shown whole; a longer one is cut to this many characters from its end (0: no limit).</summary>
    int MaxChunkLength { get; }

    /// <summary>A control character that the terminal does nothing about is shown as its number.</summary>
    bool ShowUnrecognizedAsHex { get; }

    /// <summary>Writes a segment of text (without control characters) at the cursor: through the character set, and in byte mode with the stray bytes of a letter put together.</summary>
    void WriteOutputText(string text);

    /// <summary>BEL.</summary>
    void Bell();

    /// <summary>A line of the Debug trace (it does nothing in a Release build).</summary>
    void Trace(string text);

    /// <summary>The object that the Debug trace of the next escape sequence is written to, or null in a Release build.</summary>
    IEscapeTrace BeginEscapeTrace();

    MouseReportingMode MouseMode { set; }

    string Title { set; }

    string ReportedDirectory { set; }
}
