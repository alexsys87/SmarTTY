using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using VisualTTY.DeviceTerminal;

using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace SmarTTY.GUI.DevicePanel;

// A netconsole receiver (Linux netconsole, U-Boot netconsole): the console output of the device arrives as UDP datagrams
// and is shown as it comes; there is no shell prompt to continue, so the terminal never shows its own input line.
// Input goes back as UDP datagrams in one of two ways, switched in the "Netconsole" menu of the terminal:
//  - line input: the line is typed in a box below the terminal (with the saved history on Up/Down) and sent as one
//    datagram when Enter is pressed, so the kernel messages that keep arriving never mix with what is being typed;
//  - character input: every key is sent at once, as a datagram of its own (U-Boot's netconsole reads it like a serial
//    console).
public partial class WpfDeviceTerminal : INetconsoleLineHost
{
    private NetconsoleTransport _netconsoleTransport;

    // The bar of line input below the terminal (WPF since stage 4A): the label, the box that takes the width left, the button, the target.
    private System.Windows.Controls.Border _netconsoleBar;

    private System.Windows.Controls.TextBlock _netconsoleBarLabel;

    private System.Windows.Controls.TextBox _netconsoleInput;

    private System.Windows.Controls.Button _netconsoleSendButton;

    private System.Windows.Controls.TextBlock _netconsoleTargetLabel;

    private MenuItem _netconsoleMenu;

    private MenuItem _netconsoleLineModeItem;

    private MenuItem _netconsoleCharacterModeItem;

    private MenuItem _netconsoleTargetItem;

    // The history walk of the input box and what is typed into the terminal in line mode (VisualTTY/DeviceTerminal/NetconsoleLineInput.cs).
    private NetconsoleLineInput _netconsoleLine;

    private NetconsoleLineInput NetconsoleLine => _netconsoleLine ?? (_netconsoleLine = new NetconsoleLineInput(this));

    // Whether what is sent is also shown in the terminal (the Linux module never echoes, U-Boot does).
    private bool _netconsoleEcho;

    private DeviceTerminalSettings.ConnectionSettings.Netconsole NetconsoleSettings => _deviceTerminalSettings?.Connection as DeviceTerminalSettings.ConnectionSettings.Netconsole;

    /// <summary>True when this terminal is a netconsole receiver.</summary>
    public bool IsNetconsole => NetconsoleSettings != null;

    /// <summary>True when typed input is sent line by line (from the box below the terminal).</summary>
    public bool NetconsoleLineInput => NetconsoleSettings?.Input == DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line;

    /// <summary>The text box of line input (for tests and for focusing it).</summary>
    internal System.Windows.Controls.TextBox NetconsoleInputBox => _netconsoleInput;

    private void InitializeNetconsoleSupport()
    {
        const string help = "Line input: the line is sent as one UDP datagram when Enter is pressed. Up/Down: history. Esc: clear. Ctrl+C with nothing selected: send Ctrl+C.";
        _netconsoleBarLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Send:",
            ToolTip = help,
            Margin = new System.Windows.Thickness(2, 0, 4, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _netconsoleInput = new System.Windows.Controls.TextBox { ToolTip = help, MinWidth = 80, VerticalContentAlignment = System.Windows.VerticalAlignment.Center };
        _netconsoleInput.PreviewKeyDown += OnNetconsoleInputKeyDown;
        _netconsoleSendButton = new System.Windows.Controls.Button
        {
            Content = ImageAndText("upload", "Send"),
            ToolTip = "Send the line (Enter)",
            Margin = new System.Windows.Thickness(4, 0, 0, 0)
        };
        _netconsoleSendButton.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, System.Windows.Controls.ToolBar.ButtonStyleKey);
        _netconsoleSendButton.Click += (sender, e) => SendNetconsoleLine();
        _netconsoleTargetLabel = new System.Windows.Controls.TextBlock
        {
            Margin = new System.Windows.Thickness(8, 0, 2, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        System.Windows.Controls.DockPanel row = new System.Windows.Controls.DockPanel { LastChildFill = true, Margin = new System.Windows.Thickness(4, 2, 4, 2) };
        System.Windows.Controls.DockPanel.SetDock(_netconsoleBarLabel, System.Windows.Controls.Dock.Left);
        System.Windows.Controls.DockPanel.SetDock(_netconsoleTargetLabel, System.Windows.Controls.Dock.Right);
        System.Windows.Controls.DockPanel.SetDock(_netconsoleSendButton, System.Windows.Controls.Dock.Right);
        row.Children.Add(_netconsoleBarLabel);
        row.Children.Add(_netconsoleTargetLabel);
        row.Children.Add(_netconsoleSendButton);
        row.Children.Add(_netconsoleInput);
        // The colours of a tool bar of the theme: its background, its text and the line between it and the terminal.
        _netconsoleBar = new System.Windows.Controls.Border { Child = row, BorderThickness = new System.Windows.Thickness(0, 1, 0, 0) };
        _netconsoleBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Theme.SurfaceBackground");
        _netconsoleBar.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Theme.Divider");
        _netconsoleBar.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Theme.Text");
        AddNetconsoleBar(_netconsoleBar);
    }

    private static object ImageAndText(string icon, string text)
    {
        System.Windows.Controls.StackPanel content = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        System.Windows.Controls.Image image = WpfMenus.Icon(icon);
        if (image != null)
        {
            image.Margin = new System.Windows.Thickness(0, 0, 4, 0);
            content.Children.Add(image);
        }
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = System.Windows.VerticalAlignment.Center });
        return content;
    }

    /// <summary>Called from ApplySettings: the menu, the input mode and the echo of a netconsole receiver (or none of them).</summary>
    private void ConfigureNetconsole()
    {
        DeviceTerminalSettings.ConnectionSettings.Netconsole settings = NetconsoleSettings;
        UpdateNetconsoleMenu(settings != null);
        if (settings == null)
        {
            _serialTerminalControl.InputLineDisabled = false;
            if (!(_deviceTerminalSettings?.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet))
            {
                // Telnet sets its own echo when it connects.
                _serialTerminalControl.EchoRawInput = false;
            }
            UpdateNetconsoleBarVisibility();
            return;
        }
        _netconsoleEcho = settings.LocalEcho;
        // Nothing to continue: the device prints log messages, not a prompt that a local input line could follow.
        _serialTerminalControl.InputLineDisabled = true;
        // The terminal's LocalEcho sends every typed character on top of the line; netconsole sends either one or the other.
        _serialTerminalControl.LocalEcho = false;
        ApplyNetconsoleInputMode();
    }

    /// <summary>Applies the input mode of the settings to the terminal, the input box, the menu and the open transport.</summary>
    private void ApplyNetconsoleInputMode()
    {
        DeviceTerminalSettings.ConnectionSettings.Netconsole settings = NetconsoleSettings;
        if (settings == null)
        {
            return;
        }
        bool lineInput = settings.Input == DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line;
        // In line mode the text is shown when it is sent; in character mode the terminal draws every key.
        _serialTerminalControl.EchoRawInput = !lineInput && _netconsoleEcho;
        echoTypedCharactersToolStripMenuItem.IsChecked = _netconsoleEcho;
        NetconsoleTransport transport = _netconsoleTransport;
        if (transport != null)
        {
            transport.InputMode = settings.Input;
        }
        if (_netconsoleLineModeItem != null)
        {
            _netconsoleLineModeItem.IsChecked = lineInput;
            _netconsoleCharacterModeItem.IsChecked = !lineInput;
        }
        UpdateNetconsoleBarVisibility();
        RefreshStatusIfChanged();
    }

    /// <summary>The input box is shown for line input in the text view (the HEX view has its own send box).</summary>
    private void UpdateNetconsoleBarVisibility()
    {
        if (_netconsoleBar == null)
        {
            return;
        }
        bool visible = NetconsoleLineInput && !HexMode;
        if (_netconsoleBarShown != visible)
        {
            SetNetconsoleBarVisible(visible);
        }
        if (visible)
        {
            UpdateNetconsoleTarget();
        }
    }

    /// <summary>Switches between line and character input for this tab.</summary>
    public void SetNetconsoleInputMode(DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode mode)
    {
        DeviceTerminalSettings.ConnectionSettings.Netconsole settings = NetconsoleSettings;
        if (settings == null)
        {
            return;
        }
        settings.Input = mode;
        ApplyNetconsoleInputMode();
        if (mode == DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line)
        {
            if (_netconsoleBarShown)
            {
                    System.Windows.Input.Keyboard.Focus(_netconsoleInput);
            }
        }
        else
        {
            // What was typed in the box stays there for when line input comes back.
            _serialTerminalControl.FocusTerminal();
        }
    }

    private void ToggleNetconsoleEcho()
    {
        _netconsoleEcho = !_netconsoleEcho;
        DeviceTerminalSettings.ConnectionSettings.Netconsole settings = NetconsoleSettings;
        if (settings != null)
        {
            settings.LocalEcho = _netconsoleEcho;
        }
        ApplyNetconsoleInputMode();
    }

    /// <summary>Called by Connect for every new transport (null when the connection is not a netconsole one).</summary>
    private void AttachNetconsole(NetconsoleTransport transport)
    {
        _netconsoleTransport = transport;
        if (transport == null)
        {
            return;
        }
        DeviceTerminalSettings.ConnectionSettings.Netconsole settings = NetconsoleSettings;
        if (settings != null)
        {
            transport.InputMode = settings.Input;
        }
        transport.SenderChanged += (sender, e) => OnNetconsoleSenderChanged(transport);
        UpdateNetconsoleTarget();
    }

    // Raised on the reader thread.
    private void OnNetconsoleSenderChanged(NetconsoleTransport transport)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && ReferenceEquals(transport, _netconsoleTransport))
                {
                    UpdateNetconsoleTarget();
                    RefreshStatusIfChanged();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The tab is being closed.
        }
    }

    /// <summary>"device:port" input goes to now, or why there is none.</summary>
    private string DescribeNetconsoleTarget()
    {
        IPEndPoint target = _netconsoleTransport?.InputTarget;
        if (target != null)
        {
            return DeviceTerminalSettings.ConnectionSettings.Tcp.FormatEndpoint(target.Address.ToString(), target.Port);
        }
        return null;
    }

    private void UpdateNetconsoleTarget()
    {
        if (_netconsoleTargetLabel == null)
        {
            return;
        }
        string target = DescribeNetconsoleTarget();
        _netconsoleTargetLabel.Text = target != null ? "to " + target : "to the first sender";
        _netconsoleTargetLabel.ToolTip = target != null
            ? "Typed input is sent to " + target + " (UDP)."
            : "No datagram has arrived yet. Input goes to the address the first one comes from, or set the device address in the connection settings.";
    }

    private string NetconsoleStatusText(string name)
    {
        string mode = NetconsoleLineInput ? "line input" : "character input";
        IPEndPoint sender = _netconsoleTransport?.LastSender;
        return sender == null ? name + ": listening (" + mode + ")" : name + ": listening (" + mode + ", last message from " + sender.Address + ")";
    }

    private void UpdateNetconsoleMenu(bool netconsole)
    {
        if (!netconsole)
        {
            if (_netconsoleMenu != null)
            {
                _netconsoleMenu.SetVisible(false);
            }
            return;
        }
        if (_netconsoleMenu == null)
        {
            _netconsoleMenu = new DeviceMenuItem("Netconsole");
            _netconsoleMenu.Icon = WpfMenus.Icon("network");
            _netconsoleLineModeItem = new DeviceMenuItem("Line input (send each line on Enter)", null,
                (sender, e) => SetNetconsoleInputMode(DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line))
            {
                ToolTip = "The line is typed in the box below the terminal and sent as one UDP datagram when Enter is pressed."
            };
            _netconsoleCharacterModeItem = new DeviceMenuItem("Character input (send every key at once)", null,
                (sender, e) => SetNetconsoleInputMode(DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Character))
            {
                ToolTip = "Every key typed in the terminal is sent at once, as a UDP datagram of its own."
            };
            MenuItem sendCtrlC = new DeviceMenuItem("Send Ctrl+C", null, (sender, e) => SendNetconsoleBytes(new byte[] { 3 }));
            _netconsoleTargetItem = new MenuItem { IsEnabled = false };
            foreach (System.Windows.Controls.Control item in new System.Windows.Controls.Control[]
            {
                _netconsoleLineModeItem, _netconsoleCharacterModeItem, new Separator(), sendCtrlC, new Separator(), _netconsoleTargetItem
            })
            {
                _netconsoleMenu.Items.Add(item);
            }
            _netconsoleMenu.SubmenuOpened += (sender, e) =>
            {
                bool connected = _connectionState == ConnectionStateEnum.Connected;
                sendCtrlC.IsEnabled = connected;
                string target = DescribeNetconsoleTarget();
                IPEndPoint last = _netconsoleTransport?.LastSender;
                _netconsoleTargetItem.Header = WpfMenus.PlainText((target != null ? "Input goes to " + target : "Input goes to the first sender (nothing received yet)")
                    + (last != null ? "; last message from " + last.Address : ""));
            };
            contextMenuStrip1.Items.Add(_netconsoleMenu);
        }
        _netconsoleMenu.SetVisible(true);
    }

    /// <summary>Sends bytes to the device as they are (Ctrl+C from the menu or the input box).</summary>
    private void SendNetconsoleBytes(byte[] bytes)
    {
        if (IsLineClaimed || _asyncTransportConnection == null)
        {
            return;
        }
        SendToDevice(bytes, bytes.Length);
    }

    /// <summary>Sends the line in the input box with the line ending of the connection, as one datagram.</summary>
    internal void SendNetconsoleLine()
    {
        if (_netconsoleInput == null || IsLineClaimed || _asyncTransportConnection == null || _connectionState != ConnectionStateEnum.Connected)
        {
            return;
        }
        string line = _netconsoleInput.Text;
        string ending = GetLineEnding(_deviceTerminalSettings.LineEnding) ?? "\n";
        byte[] bytes = _textEncoding.GetBytes(line + ending);
        if (!SendToDevice(bytes, bytes.Length))
        {
            return;
        }
        if (_netconsoleEcho)
        {
            if (HexMode)
            {
                hexTerminalControl1.AppendData(bytes, bytes.Length, isEcho: true);
            }
            else
            {
                _serialTerminalControl.ShowLocalText(line + "\r\n");
            }
        }
        RememberNetconsoleLine(line);
        _netconsoleInput.Text = "";
        NetconsoleLine.Reset();
    }

    /// <summary>Adds a sent line to the history of this tab and to the history saved for the port.</summary>
    private void RememberNetconsoleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        _serialTerminalControl.AddToHistory(line);
        // A line typed with a leading blank is not saved (like in a shell).
        _commandLists.Remember(line);
    }

    private void OnNetconsoleInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        System.Windows.Input.ModifierKeys modifiers = System.Windows.Input.Keyboard.Modifiers;
        bool control = (modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool alt = (modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
        bool shift = (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        switch (e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key)
        {
            case System.Windows.Input.Key.Return when !control && !alt:
                SendNetconsoleLine();
                break;
            case System.Windows.Input.Key.Escape:
                _netconsoleInput.Text = "";
                NetconsoleLine.Reset();
                break;
            case System.Windows.Input.Key.Up when !control && !alt:
                NetconsoleLine.Older();
                break;
            case System.Windows.Input.Key.Down when !control && !alt:
                NetconsoleLine.Newer();
                break;
            case System.Windows.Input.Key.C when control && !alt && !shift && _netconsoleInput.SelectionLength == 0:
                // Nothing to copy: Ctrl+C goes to the device (U-Boot stops a running command with it).
                SendNetconsoleBytes(new byte[] { 3 });
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void SetNetconsoleInputText(string text)
    {
        _netconsoleInput.Text = text ?? "";
        _netconsoleInput.SelectionStart = _netconsoleInput.Text.Length;
        _netconsoleInput.SelectionLength = 0;
    }

    /// <summary>
    /// Line input: what is typed or pasted into the terminal itself goes into the input box instead of to the device
    /// (the keyboard then stays in the box). Enter sends the line, Backspace edits it; control keys and escape sequences
    /// (Ctrl+C, the arrows) still go to the device at once. False when the data is to be sent as it is.
    /// </summary>
    private bool RedirectToNetconsoleLine(byte[] buffer, TerminalInputKind inputKind)
    {
        if (!NetconsoleLineInput || _netconsoleInput == null || HexMode)
        {
            return false;
        }
        if (inputKind != TerminalInputKind.Typed && inputKind != TerminalInputKind.Paste)
        {
            return false;
        }
        NetconsoleLine.Process(_textEncoding.GetString(buffer));
        if (_netconsoleBarShown && !_netconsoleInput.IsKeyboardFocused)
        {
            System.Windows.Input.Keyboard.Focus(_netconsoleInput);
            _netconsoleInput.SelectionStart = _netconsoleInput.Text.Length;
            _netconsoleInput.SelectionLength = 0;
        }
        return true;
    }

    string INetconsoleLineHost.InputText => _netconsoleInput.Text;

    void INetconsoleLineHost.SetInputText(string text)
    {
        SetNetconsoleInputText(text);
    }

    void INetconsoleLineHost.SendLine()
    {
        SendNetconsoleLine();
    }

    void INetconsoleLineHost.SendBytes(byte[] bytes)
    {
        SendNetconsoleBytes(bytes);
    }

    byte[] INetconsoleLineHost.Encode(string text)
    {
        return _textEncoding.GetBytes(text);
    }

    IReadOnlyList<string> INetconsoleLineHost.History => _serialTerminalControl.CommandHistory;
}
