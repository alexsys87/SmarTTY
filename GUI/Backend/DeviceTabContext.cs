using SmarTTY.Controls;
using SmarTTY.Ssh;
using SmarTTY.Transport;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Forms.Integration;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.Backend;

/// <summary>
/// A tab that hosts a COM-port or raw-TCP terminal (<see cref="DeviceTerminalControl"/>) inside the main window,
/// next to the SSH sessions. Unlike an SSH session, a device connection is not multiplexed: the tab owns the
/// transport and a device session never offers "open another terminal tab".
/// </summary>
public class DeviceTabContext : ITabContext, IDisposable, INotifyPropertyChanged, ICloseableElement
{
    private readonly SessionWrapper _Session;

    private bool _SettingsApplied;

    public readonly DeviceTerminalControl Terminal;

    public DeviceTerminalSettings Settings { get; }

    public FrameworkElement Control { get; }

    public FrameworkElement ToolBar => null;

    public FrameworkElement StatusBar => null;

    public TextEditorControl EditorControl => null;

    public string Title => DeviceTerminalControl.GetConnectionTitle(Settings.Connection);

    public IconType Icon => (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial) ? IconType.Serial : IconType.Network;

    /// <summary>
    /// The shared search bar searches the view that is currently shown by the device terminal: the decoded text in
    /// ASCII mode, and in HEX mode the HEX dump editor (whose text holds lowercase byte pairs, so byte sequences
    /// typed as "0A 0D" or "0a0d" are matched as well).
    /// </summary>
    public TextSearcher Searcher { get; }

    public string ToolTip => Terminal.StatusText;

    public bool IsNewSessionShortcut => false;

    public FileExplorer FileExplorer => null;

    public FrameworkElement VerticalScrollBar => null;

    public FrameworkElement HorizontalScrollBar => null;

    /// <summary>
    /// Identifies the physical resource behind the tab, or null when the same endpoint may be opened several times.
    /// A COM port can be opened by one owner only, so two tabs for the same port are never created.
    /// </summary>
    public string ExclusiveKey => MakeExclusiveKey(Settings.Connection);

    public event PropertyChangedEventHandler PropertyChanged;

    public DeviceTabContext(SessionWrapper session, DeviceTerminalSettings settings)
    {
        _Session = session;
        Settings = settings;
        Terminal = new DeviceTerminalControl();
        Terminal.StatusChanged += OnTerminalStatusChanged;
        Terminal.CloseTabRequested += OnTerminalCloseTabRequested;
        Terminal.FullScreenToggleRequested += OnTerminalFullScreenToggleRequested;
        Terminal.DisplayModeChanged += OnTerminalDisplayModeChanged;
        WinFormsHostWrapper host = new WinFormsHostWrapper();
        ((WindowsFormsHost)host).Child = Terminal;
        Control = host;
        // Search resolves the current view and query normalization lazily, so one searcher covers the ASCII text
        // view and the HEX dump editor, switching automatically when the user toggles HEX mode.
        Searcher = new TextSearcher(() => Terminal.SearchTextEditor, q => Terminal.HexMode ? NormalizeHexQuery(q) : q);
        // The transport is opened when the control gets its window, exactly as the standalone terminal window did
        // in OnShown: output that arrives for a control without a handle is the thing to avoid.
        if (Terminal.IsHandleCreated)
        {
            ApplySettingsOnce();
        }
        else
        {
            Terminal.HandleCreated += Terminal_HandleCreated;
        }
    }

    /// <summary>Returns a key that is equal for two settings only when they would open the same exclusive resource.</summary>
    public static string MakeExclusiveKey(DeviceTerminalSettings.ConnectionSettings connection)
    {
        return connection is DeviceTerminalSettings.ConnectionSettings.Serial serial ? DeviceSessionKey.ForSerialPort(serial.ComPortName) : null;
    }

    private void Terminal_HandleCreated(object sender, EventArgs e)
    {
        Terminal.HandleCreated -= Terminal_HandleCreated;
        ApplySettingsOnce();
    }

    private void ApplySettingsOnce()
    {
        if (_SettingsApplied)
        {
            return;
        }
        _SettingsApplied = true;
        // Shared font/palette/key behavior first: ApplySettings opens the transport, and received text must be
        // styled with the final palette from the very first byte. Device colors are applied right after.
        ApplyCommonSettings();
        Terminal.ApplySettings(Settings);
    }

    /// <summary>Applies the shared (SSH-wide) appearance and key behavior; per-device settings are left alone.</summary>
    public void ApplyCommonSettings()
    {
        Terminal.ApplyCommonSettings(_Session.CommonSettings);
    }

    private void OnTerminalCloseTabRequested(object sender, EventArgs e)
    {
        _Session.CloseDeviceTabFromKeyboard(this);
    }

    private void OnTerminalFullScreenToggleRequested(object sender, EventArgs e)
    {
        _Session.ToggleFullScreenFromDevice();
    }

    private void OnTerminalStatusChanged(object sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ToolTip"));
        _Session.OnDeviceStatusChanged();
    }

    /// <summary>
    /// Stores a mode picked in the toolbar in the saved profile itself. Auto-detected COM ports that have no saved
    /// profile are skipped: there is nothing to update and no file must be created behind the user's back.
    /// </summary>
    private void OnTerminalDisplayModeChanged(object sender, EventArgs e)
    {
        try
        {
            if (Settings?.Connection == null)
            {
                return;
            }
            string key = Settings.Connection.FileNameBase;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            foreach (RawConnectionSettings saved in new SavedConnectionStore().RawConnections)
            {
                if (saved?.Settings?.Connection == null || string.IsNullOrEmpty(saved.FileName))
                {
                    continue;
                }
                if (!string.Equals(saved.Settings.Connection.FileNameBase, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                saved.Settings.DisplayMode = Settings.DisplayMode;
                if (saved.Settings.HexSettings == null)
                {
                    saved.Settings.HexSettings = new DeviceTerminalSettings.HexTerminalSettings();
                }
                saved.Settings.HexSettings.ShowTextView = Settings.HexSettings?.ShowTextView
                    ?? (Settings.DisplayMode == DeviceTerminalSettings.CharacterDisplayMode.Both);
                saved.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Could not save the display mode of the device profile: " + ex.Message);
        }
    }

    public void FocusMainControl()
    {
        Terminal?.FocusTerminal();
    }

    public void OnSessionInactiveChanged(bool isInactive)
    {
    }

    public void OnSessionReconnected(SshSession session)
    {
    }

    public void Close()
    {
        _Session.CloseDeviceTab(this);
    }

    public void Dispose()
    {
        Terminal.HandleCreated -= Terminal_HandleCreated;
        Terminal.StatusChanged -= OnTerminalStatusChanged;
        Terminal.CloseTabRequested -= OnTerminalCloseTabRequested;
        Terminal.FullScreenToggleRequested -= OnTerminalFullScreenToggleRequested;
        Terminal.DisplayModeChanged -= OnTerminalDisplayModeChanged;
        Terminal.Dispose();
    }

    /// <summary>
    /// Rewrites a HEX search query into the form stored by the HEX dump editor: lowercase byte pairs separated by
    /// single spaces ("0a 0d"). Returns the query unchanged when it is not a plain HEX byte sequence (an odd number
    /// of digits, an empty string or any non-HEX character such as a letter beyond a-f) or when it already can be
    /// matched as typed.
    /// </summary>
    private static string NormalizeHexQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return query;
        }
        string digits = query.Replace(" ", "").Replace("\t", "");
        if (digits.Length == 0 || (digits.Length & 1) != 0)
        {
            return query;
        }
        for (int i = 0; i < digits.Length; i++)
        {
            if (!Uri.IsHexDigit(digits[i]))
            {
                return query;
            }
        }
        StringBuilder builder = new StringBuilder(digits.Length + digits.Length / 2);
        for (int i = 0; i < digits.Length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }
            builder.Append(char.ToLowerInvariant(digits[i])).Append(char.ToLowerInvariant(digits[i + 1]));
        }
        return builder.ToString();
    }
}
