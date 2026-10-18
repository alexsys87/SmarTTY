using SmarTTY.Controls;
using SmarTTY.Ssh;
using System;
using System.ComponentModel;
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

    public TextEditorControl EditorControl => null;

    public string Title => DeviceTerminalControl.GetConnectionTitle(Settings.Connection);

    public IconType Icon => (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial) ? IconType.Serial : IconType.Network;

    public TextSearcher Searcher => null;

    public string ToolTip => Terminal.StatusText;

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
        WinFormsHostWrapper host = new WinFormsHostWrapper();
        ((WindowsFormsHost)host).Child = Terminal;
        Control = host;
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
        if (connection is DeviceTerminalSettings.ConnectionSettings.Serial serial && !string.IsNullOrWhiteSpace(serial.ComPortName))
        {
            return "serial:" + serial.ComPortName.Trim().ToUpperInvariant();
        }
        return null;
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
        Terminal.Dispose();
    }
}
