namespace SmarTTY.Controls;

/// <summary>
/// The "Tools -> TFTP server / U-Boot upload" window of a device terminal: it serves a firmware file over TFTP and
/// types the U-Boot commands into the terminal's connection that make the board fetch it. The device terminal is
/// compiled by the Mono test harness, which cannot reference the themed WPF window, so the window is injected
/// through <see cref="DeviceTerminalControl.TftpFirmwareWindowFactory"/>.
/// </summary>
public interface ITftpFirmwareWindow
{
    /// <summary>True while the window is open. A closed window cannot be shown again, so a new one is made.</summary>
    bool IsOpen { get; }

    /// <summary>Brings the open window to the front.</summary>
    void Activate();

    /// <summary>Shows the modeless window over the active window. Must be called on the UI thread.</summary>
    void Show();

    /// <summary>Closes the window because the terminal it belongs to is gone: nobody is left to ask.</summary>
    void CloseWithTerminal();
}
