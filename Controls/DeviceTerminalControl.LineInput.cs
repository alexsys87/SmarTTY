using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// Saved command history and user command lists of a COM/TCP terminal. The line editor itself (Up/Down, Ctrl+R,
// Tab completion) lives in SerialTerminalControl; this part gives it what it cannot keep on its own:
//  - the history of every device is stored on disk and comes back the next time the same port or host is opened
//    (what was typed at a "Password:" prompt is not stored);
//  - the user can keep command lists, one for this device and one shared by all devices; together with the saved
//    history they are offered by Tab while the first word of a line is typed.
public partial class DeviceTerminalControl
{
    private string _commandKey;

    private DeviceLineInputStorage _commandStorage;

    private CommandHistory _savedHistory = new CommandHistory();

    private List<string> _deviceCommands = new List<string>();

    private List<string> _commonCommands = new List<string>();

    private ToolStripMenuItem _editDeviceCommandsMenuItem;

    private void InitializeCommandLists()
    {
        _serialTerminalControl.CommandSubmitted += OnCommandSubmitted;

        _editDeviceCommandsMenuItem = new ToolStripMenuItem("Edit commands for this device...");
        _editDeviceCommandsMenuItem.Click += (sender, e) => EditCommandList(forThisDevice: true);
        ToolStripMenuItem editCommon = new ToolStripMenuItem("Edit commands for all devices...");
        editCommon.Click += (sender, e) => EditCommandList(forThisDevice: false);
        ToolStripMenuItem clearHistory = new ToolStripMenuItem("Clear saved command history");
        clearHistory.Click += (sender, e) => ClearSavedHistory();
        Icons.Apply(_editDeviceCommandsMenuItem, "edit");
        Icons.Apply(editCommon, "edit");
        Icons.Apply(clearHistory, "delete");
        toolStripDropDownButton1.DropDownItems.Add(new ToolStripSeparator());
        toolStripDropDownButton1.DropDownItems.Add(_editDeviceCommandsMenuItem);
        toolStripDropDownButton1.DropDownItems.Add(editCommon);
        toolStripDropDownButton1.DropDownItems.Add(clearHistory);
    }

    /// <summary>Loads the history and the command lists of this device. Called from ApplySettings before connecting.</summary>
    private void ConfigureCommandLists()
    {
        _commandKey = _deviceTerminalSettings.Connection switch
        {
            DeviceTerminalSettings.ConnectionSettings.Serial serial => DeviceLineInputStorage.MakeSerialKey(serial.ComPortName),
            DeviceTerminalSettings.ConnectionSettings.Tcp tcp => DeviceLineInputStorage.MakeTcpKey(tcp.Host, tcp.Port),
            _ => null,
        };
        _commandStorage = new DeviceLineInputStorage(GlobalSettings.DataDirectory);
        _savedHistory = _commandKey != null ? _commandStorage.LoadHistory(_commandKey) : new CommandHistory();
        _deviceCommands = _commandKey != null ? _commandStorage.LoadCommands(_commandKey) : new List<string>();
        _commonCommands = _commandStorage.LoadCommands(null);
        _editDeviceCommandsMenuItem.Enabled = _commandKey != null;

        // The terminal keeps its own in-memory history for Up/Down and Ctrl+R; start it from the saved one.
        _serialTerminalControl.ClearHistory();
        foreach (string command in _savedHistory.Items)
        {
            _serialTerminalControl.AddToHistory(command);
        }
        RefreshCommandSuggestions();
    }

    private void RefreshCommandSuggestions()
    {
        _serialTerminalControl.CommandSuggestions = CommandListFile.BuildSuggestions(_savedHistory.Items, _deviceCommands, _commonCommands);
    }

    private void OnCommandSubmitted(object sender, SerialTerminalControl.CommandSubmittedEventArgs e)
    {
        if (_commandKey == null || LineInputText.LooksLikeSecretPrompt(e.Prompt))
        {
            return;
        }
        // RawText keeps leading blanks: a command typed with a leading space is not saved.
        if (_savedHistory.Add(e.RawText))
        {
            _commandStorage.SaveHistory(_commandKey, _savedHistory);
            RefreshCommandSuggestions();
        }
    }

    private void EditCommandList(bool forThisDevice)
    {
        string key = forThisDevice ? _commandKey : null;
        if (forThisDevice && key == null)
        {
            return;
        }
        string title = forThisDevice ? "Commands - " + ConnectionTitle : "Commands for all devices";
        string description = (forThisDevice
            ? "Commands offered by Tab for " + ConnectionTitle + ".\r\n"
            : "Commands offered by Tab for every COM and TCP device.\r\n")
            + "One command per line, lines starting with # are comments. Press Tab while typing the first word of a line.";
        List<string> commands = EditCommandList(title, description, forThisDevice ? _deviceCommands : _commonCommands);
        if (commands == null)
        {
            return;
        }
        if (!_commandStorage.SaveCommands(key, commands))
        {
            ThemedMessageBox.Show("The command list could not be saved.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (forThisDevice)
        {
            _deviceCommands = commands;
        }
        else
        {
            _commonCommands = commands;
        }
        RefreshCommandSuggestions();
    }

    /// <summary>
    /// Shows the command-list editor through <see cref="CommandListEditor"/>, which <c>App</c> wires to the themed WPF
    /// window. Returns null when the editor is unavailable (the Mono test harness) or the user cancelled.
    /// </summary>
    private static List<string> EditCommandList(string title, string description, List<string> commands)
    {
        Func<string, string, IEnumerable<string>, List<string>> editor = CommandListEditor;
        return editor != null ? editor(title, description, commands) : null;
    }

    private void ClearSavedHistory()
    {
        if (_savedHistory.Count == 0 && _serialTerminalControl.CommandHistory.Count == 0)
        {
            return;
        }
        if (ThemedMessageBox.Show("Delete the saved command history of " + ConnectionTitle + "?", "Command history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        _savedHistory.Clear();
        if (_commandKey != null)
        {
            _commandStorage.DeleteHistory(_commandKey);
        }
        _serialTerminalControl.ClearHistory();
        RefreshCommandSuggestions();
    }
}
