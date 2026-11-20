using SmarTTY.Terminal.Input;
using SmarTTY.GUI.TerminalView;
using SmarTTY.TextEditing;
using SmarTTY.Controls;
using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI.DevicePanel;

/// <summary>
/// The COM/TCP/telnet/netconsole tab on WPF (stage 2D): what <c>DeviceTerminalControl</c> did, with the WPF serial terminal
/// (<see cref="WpfSerialTerminal"/>) and HEX view (<see cref="WpfHexTerminal"/>), and the "connection closed" prompt of the SSH sessions
/// shown over them. The tool bar, the netconsole bar and the menus are still the WinForms ones of the old panel (hosted in a
/// <see cref="WindowsFormsHost"/> strip and shown as popups): they go to WPF with the other menus in stage 4. The logic of the partial
/// files is that of the WinForms panel, moved as it was; this file is what was the control: the layout, the thread of the window, the
/// owner of the dialogs and the keys.
/// </summary>
public partial class WpfDeviceTerminal
{
    // The windows of the panel, set by App (they were hooks of the WinForms panel, which the Mono stands compiled without WPF).

    /// <summary>Replays a recorded raw byte log (the bytes, this terminal).</summary>
    public static Action<byte[], IDeviceReplayTarget> LogReplayPrompt;

    /// <summary>
    /// Lets the user change the serial port settings (the settings to edit, or null to keep the driver's; the port name). Returns whether the
    /// dialog was accepted and the chosen settings (null to keep the driver's settings).
    /// </summary>
    public static Func<DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings, string, (bool Accepted, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings Settings)> SerialSettingsPrompt;

    /// <summary>Creates the progress window of a file transfer (the title, whether the error count is shown); null: a transfer runs without one.</summary>
    public static Func<string, bool, ITransferProgressWindow> TransferProgressWindowFactory;

    /// <summary>Edits a command list of the line input (the title, the explanation, the commands); null when the user cancelled.</summary>
    public static Func<string, string, IEnumerable<string>, List<string>> CommandListEditor;

    /// <summary>Asks how dropped files are to be sent (their full paths); null when the user cancelled. Null hook: a drop is ignored.</summary>
    public static Func<string[], DeviceFileDropProtocol?> FileDropProtocolPrompt;

    /// <summary>Asks for the gzip + base64 transfer of a dropped file (its full path); null when the user cancelled.</summary>
    public static Func<string, GzipBase64Transfer.Request> GzipBase64DropPrompt;

    /// <summary>The same for the raw form, gzip + cat.</summary>
    public static Func<string, GzipBase64Transfer.Request> GzipCatDropPrompt;

    /// <summary>Asks what to transfer with gzip + base64: true to send a file, false to receive one; null when the user cancelled.</summary>
    public static Func<bool, GzipBase64Transfer.Request> GzipBase64RequestPrompt;

    /// <summary>The same for the raw form, gzip + cat.</summary>
    public static Func<bool, GzipBase64Transfer.Request> GzipCatRequestPrompt;

    /// <summary>Creates the TFTP/U-Boot window of a terminal (the terminal, its title); null when the window is unavailable.</summary>
    public static Func<IDeviceConsole, string, ITftpFirmwareWindow> TftpFirmwareWindowFactory;

    // The tool bar row (WinForms), the area of the terminals with the prompt over them, and the netconsole bar below them.
    private WindowsFormsHost _toolStripHost;

    private WindowsFormsHost _netconsoleBarHost;

    private Grid _terminalArea;

    private ConnectionClosedPopup _connectionPromptView;

    private bool _disposed;

    private WindowOwner _owner;

    /// <summary>The window of the panel as the owner of the WinForms dialogs and of the clipboard calls.</summary>
    private sealed class WindowOwner : System.Windows.Forms.IWin32Window
    {
        private readonly WpfDeviceTerminal _panel;

        public WindowOwner(WpfDeviceTerminal panel)
        {
            _panel = panel;
        }

        public IntPtr Handle => (PresentationSource.FromVisual(_panel) as HwndSource)?.Handle ?? IntPtr.Zero;
    }

    private System.Windows.Forms.IWin32Window Owner => (System.Windows.Forms.IWin32Window)_serialTerminalControl?.PasteAnchor ?? _owner ?? (_owner = new WindowOwner(this));

    private IntPtr Handle => Owner.Handle;

    private bool IsDisposed => _disposed;

    // The WinForms panel asked whether its window existed before it posted work to it; the dispatcher of a WPF element is always there.
    private bool IsHandleCreated => !_disposed;

    // What Control.BeginInvoke did: the work runs later on the thread of the window.
    private void BeginInvoke(Delegate method)
    {
        if (_disposed)
        {
            throw new InvalidOperationException("The device terminal is closed.");
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, method);
    }

    private object Invoke(Delegate method)
    {
        return Dispatcher.Invoke(method);
    }

    private void BuildLayout()
    {
        LastChildFill = true;
        Focusable = false;
        _toolStripHost = new WindowsFormsHost { Child = toolStrip1 };
        SetDock(_toolStripHost, Dock.Top);
        Children.Add(_toolStripHost);
        _terminalArea = new Grid();
        _connectionPromptView = new ConnectionClosedPopup
        {
            DataContext = _connectionPrompt,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_connectionPromptView, 10);
        _terminalArea.Children.Add(_connectionPromptView);
        Children.Add(_terminalArea);
    }

    // The terminal area holds the text terminal and the HEX view, one of which is shown.
    private void AddTerminalView(UIElement view)
    {
        _terminalArea.Children.Insert(0, view);
    }

    /// <summary>Puts the netconsole bar (WinForms) below the terminals.</summary>
    private void AddNetconsoleBar(System.Windows.Forms.Control bar)
    {
        _netconsoleBarHost = new WindowsFormsHost { Child = bar, Height = Math.Max(24, bar.Height) };
        SetDock(_netconsoleBarHost, Dock.Bottom);
        // Before the last child, which fills the rest.
        Children.Insert(Children.Count - 1, _netconsoleBarHost);
        bar.SizeChanged += (sender, e) => _netconsoleBarHost.Height = Math.Max(24, bar.Height);
        SetNetconsoleBarVisible(false);
    }

    // Whether the netconsole bar is shown (a hosted WinForms control reports itself hidden while its tab is not shown).
    private bool _netconsoleBarShown;

    private void SetNetconsoleBarVisible(bool visible)
    {
        _netconsoleBarShown = visible;
        _netconsoleBar.Visible = visible;
        if (_netconsoleBarHost != null)
        {
            _netconsoleBarHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetConnectionPromptVisible(bool visible)
    {
        _connectionPromptView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetToolbarVisible(bool visible)
    {
        toolStrip1.Visible = visible;
        _toolStripHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The context menu of the terminals (WinForms) at a point of the screen.</summary>
    private void ShowContextMenu(System.Drawing.Point screen)
    {
        contextMenuStrip1.Show(screen);
    }

    // The keys of the terminals that the panel takes (OnTerminalKeyDown of the WinForms panel).
    private void OnWpfTerminalKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Esc stops a paste that is being sent.
            Controls.PasteJob job = _pasteJob;
            if (job != null && job.IsActive)
            {
                job.Cancel();
                e.Handled = true;
                return;
            }
        }
        System.Windows.Forms.Keys keys = (System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key);
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            keys |= System.Windows.Forms.Keys.Shift;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            keys |= System.Windows.Forms.Keys.Control;
        }
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            keys |= System.Windows.Forms.Keys.Alt;
        }
        System.Windows.Forms.KeyEventArgs args = new System.Windows.Forms.KeyEventArgs(keys);
        OnTerminalKeyDown(sender, args);
        if (args.Handled)
        {
            e.Handled = true;
        }
    }

    /// <summary>The text terminal (the window follows the mouse over it for the minimalistic mode).</summary>
    internal WpfSerialTerminal TextTerminal => _serialTerminalControl;

    /// <summary>The terminal that is shown has the keyboard focus.</summary>
    public bool TerminalHasFocus => HexMode ? hexTerminalControl1.IsFocused : _serialTerminalControl.View.IsKeyboardFocused;

    /// <summary>Closes the tab's connections and timers (the WinForms panel did it when it was disposed).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        OnPanelDisposed();
        _disposed = true;
        _serialTerminalControl.Dispose();
        hexTerminalControl1.Dispose();
        DisposeComponents(true);
        _toolStripHost.Dispose();
        _netconsoleBarHost?.Dispose();
    }
}
