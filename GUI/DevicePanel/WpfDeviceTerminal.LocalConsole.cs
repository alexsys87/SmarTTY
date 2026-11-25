using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.DevicePanel;

// A local console (cmd, PowerShell, WSL run in a Windows pseudo console, Transport/ConPtyTransport): the program edits its own command
// line and draws its own echo, so the terminal never shows its input line and every key goes to the program at once; the size of the
// console follows the tab through the transport, so no command is typed to set it. The file transfers to a device do not apply.
public partial class WpfDeviceTerminal
{
    /// <summary>True when this terminal is a local console.</summary>
    public bool IsLocalConsole => _deviceTerminalSettings?.Connection is DeviceTerminalSettings.ConnectionSettings.LocalConsole;

    /// <summary>Called from ApplySettings after <see cref="ConfigureNetconsole"/>: the input of a local console, or nothing.</summary>
    private void ConfigureLocalConsole()
    {
        bool console = IsLocalConsole;
        toolStripButton3.SetVisible(!console);
        echoTypedCharactersToolStripMenuItem.SetVisible(!console);
        if (!console)
        {
            return;
        }
        _serialTerminalControl.InputLineDisabled = true;
        _serialTerminalControl.EchoRawInput = false;
        _serialTerminalControl.LocalEcho = false;
    }
}
