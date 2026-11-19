using System;
using System.Collections.Generic;

namespace VisualTTY.DeviceTerminal;

/// <summary>
/// What <see cref="DeviceCommandLists"/> needs of the panel: the in-memory history of the input line (kept apart from the saved one, for Up/Down and Ctrl+R)
/// and the list of suggestions that Tab offers. Every member is something the panel did itself before.
/// </summary>
public interface IDeviceCommandHost
{
    /// <summary>The number of commands in the history of the input line.</summary>
    int TerminalHistoryCount { get; }

    void ClearTerminalHistory();

    void AddToTerminalHistory(string command);

    void RemoveFromTerminalHistory(IReadOnlyList<string> commands);

    /// <summary>The commands Tab offers: the recent ones first, then those of the device and of all devices.</summary>
    void SetSuggestions(IReadOnlyList<string> suggestions);
}

/// <summary>
/// The saved command history of a COM/TCP device and its command lists (one for the device, one shared by all devices). The history is stored on disk and
/// comes back the next time the same port or host is opened; what was typed at a "Password:" prompt, or with a leading blank, is not stored. Together
/// with the lists the saved history is what Tab offers while the first word of a line is typed.
/// </summary>
public sealed class DeviceCommandLists
{
    private readonly IDeviceCommandHost _host;
    private DeviceLineInputStorage _storage;
    private string _key;
    private CommandHistory _savedHistory = new CommandHistory();
    private List<string> _deviceCommands = new List<string>();
    private List<string> _commonCommands = new List<string>();

    public DeviceCommandLists(IDeviceCommandHost host)
    {
        _host = host;
    }

    /// <summary>The key of the device the history and the list are kept under, or null for a connection that has none.</summary>
    public string Key => _key;

    /// <summary>The saved history of this device.</summary>
    public CommandHistory SavedHistory => _savedHistory;

    /// <summary>The commands of the list of this device.</summary>
    public List<string> DeviceCommands => _deviceCommands;

    /// <summary>The commands of the list shared by all devices.</summary>
    public List<string> CommonCommands => _commonCommands;

    /// <summary>Loads the history and the command lists of the device with this key (null: none). Called before connecting.</summary>
    public void Configure(string key, string dataDirectory)
    {
        _key = key;
        _storage = new DeviceLineInputStorage(dataDirectory);
        _savedHistory = _key != null ? _storage.LoadHistory(_key) : new CommandHistory();
        _deviceCommands = _key != null ? _storage.LoadCommands(_key) : new List<string>();
        _commonCommands = _storage.LoadCommands(null);

        // The terminal keeps its own in-memory history for Up/Down and Ctrl+R; start it from the saved one.
        _host.ClearTerminalHistory();
        foreach (string command in _savedHistory.Items)
        {
            _host.AddToTerminalHistory(command);
        }
        Refresh();
    }

    /// <summary>Gives the terminal the commands Tab offers.</summary>
    public void Refresh()
    {
        _host.SetSuggestions(CommandListFile.BuildSuggestions(_savedHistory.Items, _deviceCommands, _commonCommands));
    }

    /// <summary>A command was sent from the input line. <paramref name="rawText"/> keeps leading blanks: a command typed with a leading space is not saved.</summary>
    public void OnSubmitted(string rawText, string prompt)
    {
        if (_key == null || LineInputText.LooksLikeSecretPrompt(prompt))
        {
            return;
        }
        Remember(rawText);
    }

    /// <summary>Adds a sent line to the saved history of the device (not when it has none, or the line is blank or starts with a blank, or is the same as the last one).</summary>
    public void Remember(string line)
    {
        if (_key != null && _savedHistory.Add(line))
        {
            _storage.SaveHistory(_key, _savedHistory);
            Refresh();
        }
    }

    /// <summary>Removes commands from the history of the terminal, from the saved history and from what Tab offers.</summary>
    public void Delete(IReadOnlyList<string> commands)
    {
        _host.RemoveFromTerminalHistory(commands);
        if (_savedHistory.Remove(commands) && _key != null)
        {
            _storage.SaveHistory(_key, _savedHistory);
        }
        Refresh();
    }

    /// <summary>Neither the saved history nor the history of the terminal holds a command: there is nothing to clear.</summary>
    public bool HasNothingToClear => _savedHistory.Count == 0 && _host.TerminalHistoryCount == 0;

    /// <summary>Clears the saved history (and its file) and the history of the terminal.</summary>
    public void ClearHistory()
    {
        _savedHistory.Clear();
        if (_key != null)
        {
            _storage.DeleteHistory(_key);
        }
        _host.ClearTerminalHistory();
        Refresh();
    }

    /// <summary>Saves a command list to its file; false when it could not be written. Null for <paramref name="forThisDevice"/> false is the list shared by all devices.</summary>
    public bool SaveList(bool forThisDevice, IEnumerable<string> commands)
    {
        return _storage.SaveCommands(forThisDevice ? _key : null, commands);
    }

    /// <summary>Takes a list as the one of this device or of all devices and tells the terminal what Tab offers.</summary>
    public void SetList(bool forThisDevice, List<string> commands)
    {
        if (forThisDevice)
        {
            _deviceCommands = commands;
        }
        else
        {
            _commonCommands = commands;
        }
        Refresh();
    }
}
