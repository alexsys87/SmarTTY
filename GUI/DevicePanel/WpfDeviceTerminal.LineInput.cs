using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.Terminal;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.DevicePanel;

// Saved command history and user command lists of a COM/TCP terminal. The line editor itself (Up/Down, Ctrl+R,
// Tab completion) lives in WpfSerialTerminal; this part gives it what it cannot keep on its own:
//  - the history of every device is stored on disk and comes back the next time the same port or host is opened
//    (what was typed at a "Password:" prompt is not stored);
//  - the user can keep command lists, one for this device and one shared by all devices; together with the saved
//    history they are offered by Tab while the first word of a line is typed.
public partial class WpfDeviceTerminal : IDeviceCommandHost
{
    // The saved history and the command lists (VisualTTY/DeviceTerminal/DeviceCommandLists.cs).
    private DeviceCommandLists _commandLists;

    private ToolStripMenuItem _editDeviceCommandsMenuItem;

    private void InitializeCommandLists()
    {
        _commandLists = new DeviceCommandLists(this);
        _serialTerminalControl.CommandSubmitted += OnCommandSubmitted;

        _editDeviceCommandsMenuItem = new ToolStripMenuItem("Edit commands for this device...");
        _editDeviceCommandsMenuItem.Click += (sender, e) => EditCommandList(forThisDevice: true);
        ToolStripMenuItem editCommon = new ToolStripMenuItem("Edit commands for all devices...");
        editCommon.Click += (sender, e) => EditCommandList(forThisDevice: false);
        ToolStripMenuItem clearHistory = new ToolStripMenuItem("Clear saved command history");
        clearHistory.Click += (sender, e) => ClearSavedHistory();
        ToolStripMenuItem showHistory = new ToolStripMenuItem("Command history...");
        showHistory.ToolTipText = "Search the commands sent to this device, put one into the input line or delete some";
        showHistory.Click += (sender, e) => ShowCommandHistory();
        Icons.Apply(_editDeviceCommandsMenuItem, "edit");
        Icons.Apply(editCommon, "edit");
        Icons.Apply(clearHistory, "delete");
        Icons.Apply(showHistory, "find");
        toolStripDropDownButton1.DropDownItems.Add(new ToolStripSeparator());
        toolStripDropDownButton1.DropDownItems.Add(showHistory);
        toolStripDropDownButton1.DropDownItems.Add(_editDeviceCommandsMenuItem);
        toolStripDropDownButton1.DropDownItems.Add(editCommon);
        toolStripDropDownButton1.DropDownItems.Add(clearHistory);
        // Also in the menu of the terminal (right button), as in SSH tabs.
        ToolStripMenuItem contextHistory = new ToolStripMenuItem("Command history...", null, (sender, e) => ShowCommandHistory())
        {
            ToolTipText = showHistory.ToolTipText
        };
        Icons.Apply(contextHistory, "find");
        contextMenuStrip1.Items.Add(contextHistory);
    }

    /// <summary>
    /// The source of the "Command history" window: the commands sent to this device, newest first. Deleting removes them
    /// from the input line (Up/Down, Ctrl+R, the grey suggestion), from the saved history of the device and from what Tab offers.
    /// </summary>
    internal CommandHistorySource CreateCommandHistorySource()
    {
        List<string> commands = _serialTerminalControl.GetCommandHistory();
        return new CommandHistorySource
        {
            Title = ConnectionTitle,
            Load = () => commands,
            Delete = DeleteFromCommandHistory,
            Insert = (string command) =>
            {
                _serialTerminalControl.PutCommand(command);
                _serialTerminalControl.FocusTerminal();
            }
        };
    }

    private void ShowCommandHistory()
    {
        CreateCommandHistorySource().Show();
    }

    internal void DeleteFromCommandHistory(IReadOnlyList<string> commands)
    {
        _commandLists.Delete(commands);
    }

    /// <summary>Loads the history and the command lists of this device. Called from ApplySettings before connecting.</summary>
    private void ConfigureCommandLists()
    {
        string commandKey = _deviceTerminalSettings.Connection switch
        {
            DeviceTerminalSettings.ConnectionSettings.Serial serial => DeviceLineInputStorage.MakeSerialKey(serial.ComPortName),
            DeviceTerminalSettings.ConnectionSettings.Tcp tcp => DeviceLineInputStorage.MakeTcpKey(tcp.Host, tcp.Port),
            DeviceTerminalSettings.ConnectionSettings.Netconsole netconsole => DeviceLineInputStorage.MakeNetconsoleKey(netconsole.LocalPort),
            _ => null,
        };
        _commandLists.Configure(commandKey, GlobalSettings.DataDirectory);
        _editDeviceCommandsMenuItem.Enabled = commandKey != null;
    }

    private void OnCommandSubmitted(object sender, WpfSerialTerminal.CommandSubmittedEventArgs e)
    {
        // RawText keeps leading blanks: a command typed with a leading space is not saved.
        _commandLists.OnSubmitted(e.RawText, e.Prompt);
    }

    private void EditCommandList(bool forThisDevice)
    {
        if (forThisDevice && _commandLists.Key == null)
        {
            return;
        }
        string title = forThisDevice ? "Commands - " + ConnectionTitle : "Commands for all devices";
        string description = (forThisDevice
            ? "Commands offered by Tab for " + ConnectionTitle + ".\r\n"
            : "Commands offered by Tab for every COM and TCP device.\r\n")
            + "One command per line, lines starting with # are comments. Press Tab while typing the first word of a line.";
        List<string> commands = EditCommandList(title, description, forThisDevice ? _commandLists.DeviceCommands : _commandLists.CommonCommands);
        if (commands == null)
        {
            return;
        }
        if (!_commandLists.SaveList(forThisDevice, commands))
        {
            ThemedMessageBox.Show("The command list could not be saved.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _commandLists.SetList(forThisDevice, commands);
    }

    /// <summary>
    /// Shows the command-list editor through <see cref="DeviceTerminalControl.CommandListEditor"/>, which <c>App</c> wires to the themed WPF
    /// window. Returns null when the editor is unavailable (the Mono test harness) or the user cancelled.
    /// </summary>
    private static List<string> EditCommandList(string title, string description, List<string> commands)
    {
        Func<string, string, IEnumerable<string>, List<string>> editor = DeviceTerminalControl.CommandListEditor;
        return editor != null ? editor(title, description, commands) : null;
    }

    private void ClearSavedHistory()
    {
        if (_commandLists.HasNothingToClear)
        {
            return;
        }
        if (ThemedMessageBox.Show("Delete the saved command history of " + ConnectionTitle + "?", "Command history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        _commandLists.ClearHistory();
    }

    int IDeviceCommandHost.TerminalHistoryCount => _serialTerminalControl.CommandHistory.Count;

    void IDeviceCommandHost.ClearTerminalHistory()
    {
        _serialTerminalControl.ClearHistory();
    }

    void IDeviceCommandHost.AddToTerminalHistory(string command)
    {
        _serialTerminalControl.AddToHistory(command);
    }

    void IDeviceCommandHost.RemoveFromTerminalHistory(IReadOnlyList<string> commands)
    {
        _serialTerminalControl.RemoveFromHistory(commands);
    }

    void IDeviceCommandHost.SetSuggestions(IReadOnlyList<string> suggestions)
    {
        _serialTerminalControl.CommandSuggestions = suggestions;
    }
}
