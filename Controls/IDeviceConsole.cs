using System;

namespace SmarTTY.Controls;

/// <summary>
/// The console line of a device terminal as the TFTP firmware window uses it (the WPF device terminal,
/// <c>GUI/DevicePanel/WpfDeviceTerminal</c>; the WinForms panel was one too until stage 2): whether the device is connected, whether a file transfer has the line, the text the device prints, and
/// lines and a break typed into it.
/// </summary>
public interface IDeviceConsole
{
    bool IsDeviceConnected { get; }

    bool IsLineOwnedByTransfer { get; }

    /// <summary>Calls <paramref name="onText"/> with the text the device prints until the result is disposed.</summary>
    IDisposable ListenToConsole(Action<string> onText);

    /// <summary>Types a line into the console; false when it could not be sent.</summary>
    bool SendConsoleLine(string line);

    /// <summary>Sends a break (Ctrl+C) to the console; false when it could not be sent.</summary>
    bool SendConsoleBreak();
}

/// <summary>A device terminal that a recorded log of received data can be played back into.</summary>
public interface IDeviceReplayTarget
{
    /// <summary>Shows <paramref name="count"/> bytes as if the device had sent them.</summary>
    void ReplayReceivedData(byte[] buffer, int count);

    /// <summary>Forgets a half-decoded character before a replay starts again.</summary>
    void ResetReplayDecoder();
}
