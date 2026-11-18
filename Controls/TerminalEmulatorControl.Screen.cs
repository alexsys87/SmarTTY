using SmarTTY.Terminal.Emulation;
using SmarTTY.TextEditing;
using System.Drawing;

namespace SmarTTY.Controls;

// The terminal as the host of its escape sequence emulator (Terminal/Emulation/TerminalEmulator.cs): every member forwards to what the control already
// had. They are implemented explicitly, so that the public surface of the control is not changed.
public partial class TerminalEmulatorControl : ITerminalHost
{
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
