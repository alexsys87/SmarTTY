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

    void MoveBackBytes(int bytes);

    MouseReportingMode MouseMode { set; }

    string Title { set; }

    string ReportedDirectory { set; }
}
