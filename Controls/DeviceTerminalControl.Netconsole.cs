using SmarTTY.Forms;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// A netconsole receiver (Linux netconsole, U-Boot netconsole): the console output of the device arrives as UDP datagrams
// and is shown as it comes; there is no shell prompt to continue, so the terminal never shows its own input line.
// Input goes back as UDP datagrams in one of two ways, switched in the "Netconsole" menu of the terminal:
//  - line input: the line is typed in a box below the terminal (with the saved history on Up/Down) and sent as one
//    datagram when Enter is pressed, so the kernel messages that keep arriving never mix with what is being typed;
//  - character input: every key is sent at once, as a datagram of its own (U-Boot's netconsole reads it like a serial
//    console).
public partial class DeviceTerminalControl
{
    private NetconsoleTransport _netconsoleTransport;

    private ToolStrip _netconsoleBar;

    private ToolStripLabel _netconsoleBarLabel;

    private ToolStripTextBox _netconsoleInput;

    private ToolStripButton _netconsoleSendButton;

    private ToolStripLabel _netconsoleTargetLabel;

    private ToolStripMenuItem _netconsoleMenu;

    private ToolStripMenuItem _netconsoleLineModeItem;

    private ToolStripMenuItem _netconsoleCharacterModeItem;

    private ToolStripMenuItem _netconsoleTargetItem;

    // The history entry Up/Down showed last in the input box; -1 while the box holds what is being typed.
    private int _netconsoleHistoryIndex = -1;

    // What was typed before Up was pressed, given back when Down goes past the newest entry.
    private string _netconsoleDraft = "";

    // Whether what is sent is also shown in the terminal (the Linux module never echoes, U-Boot does).
    private bool _netconsoleEcho;

    private DeviceTerminalSettings.ConnectionSettings.Netconsole NetconsoleSettings => _deviceTerminalSettings?.Connection as DeviceTerminalSettings.ConnectionSettings.Netconsole;

    /// <summary>True when this terminal is a netconsole receiver.</summary>
    public bool IsNetconsole => NetconsoleSettings != null;

    /// <summary>True when typed input is sent line by line (from the box below the terminal).</summary>
    public bool NetconsoleLineInput => NetconsoleSettings?.Input == DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line;

    /// <summary>The text box of line input (for tests and for focusing it).</summary>
    internal ToolStripTextBox NetconsoleInputBox => _netconsoleInput;

    private void InitializeNetconsoleSupport()
    {
        _netconsoleBar = new ToolStrip
        {
            Dock = DockStyle.Bottom,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = false,
            Visible = false,
            Name = "netconsoleBar",
            TabStop = true
        };
        _netconsoleBarLabel = new ToolStripLabel("Send:")
        {
            ToolTipText = "Line input: the line is sent as one UDP datagram when Enter is pressed. Up/Down: history. Esc: clear. Ctrl+C with nothing selected: send Ctrl+C."
        };
        _netconsoleInput = new ToolStripTextBox
        {
            AutoSize = false,
            Width = 300,
            BorderStyle = BorderStyle.FixedSingle,
            ToolTipText = _netconsoleBarLabel.ToolTipText
        };
        _netconsoleInput.KeyDown += OnNetconsoleInputKeyDown;
        _netconsoleInput.KeyPress += (sender, e) =>
        {
            // Enter and Esc are handled in KeyDown; without this the text box beeps.
            if (e.KeyChar == '\r' || e.KeyChar == '\u001b')
            {
                e.Handled = true;
            }
        };
        _netconsoleSendButton = new ToolStripButton("Send")
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = "Send the line (Enter)"
        };
        Icons.Apply(_netconsoleSendButton, "upload");
        _netconsoleSendButton.Click += (sender, e) => SendNetconsoleLine();
        _netconsoleTargetLabel = new ToolStripLabel
        {
            Alignment = ToolStripItemAlignment.Right
        };
        _netconsoleBar.Items.AddRange(new ToolStripItem[] { _netconsoleBarLabel, _netconsoleInput, _netconsoleSendButton, _netconsoleTargetLabel });
        _netconsoleBar.Resize += (sender, e) => LayoutNetconsoleBar();
        _netconsoleBar.ItemAdded += (sender, e) => LayoutNetconsoleBar();
        DpiAwareForm.ConfigureToolStrip(_netconsoleBar);
        Controls.Add(_netconsoleBar);
        // Docked first, so the terminal fills what is left above it.
        _netconsoleBar.SendToBack();
    }

    /// <summary>The input box takes the width that the label, the button and the target leave.</summary>
    private void LayoutNetconsoleBar()
    {
        if (_netconsoleBar == null || _netconsoleInput == null)
        {
            return;
        }
        int others = 0;
        foreach (ToolStripItem item in _netconsoleBar.Items)
        {
            if (item != _netconsoleInput && item.Available)
            {
                others += item.Width + item.Margin.Horizontal;
            }
        }
        int width = _netconsoleBar.DisplayRectangle.Width - others - _netconsoleInput.Margin.Horizontal - 8;
        _netconsoleInput.Width = Math.Max(80, width);
    }

    private void ApplyNetconsoleBarTheme()
    {
        if (_netconsoleBar == null || _toolbarPalette == null)
        {
            return;
        }
        _netconsoleBar.Renderer = _toolbarRenderer;
        _netconsoleBar.BackColor = _toolbarPalette.Background;
        _netconsoleBar.ForeColor = _toolbarPalette.Text;
        foreach (ToolStripItem item in _netconsoleBar.Items)
        {
            item.ForeColor = _toolbarPalette.Text;
        }
        _netconsoleInput.BackColor = _toolbarPalette.Input;
        _netconsoleInput.ForeColor = _toolbarPalette.Text;
        if (_netconsoleMenu != null)
        {
            ApplyMenuTheme(_netconsoleMenu.DropDown);
        }
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
        echoTypedCharactersToolStripMenuItem.Checked = _netconsoleEcho;
        NetconsoleTransport transport = _netconsoleTransport;
        if (transport != null)
        {
            transport.InputMode = settings.Input;
        }
        if (_netconsoleLineModeItem != null)
        {
            _netconsoleLineModeItem.Checked = lineInput;
            _netconsoleCharacterModeItem.Checked = !lineInput;
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
        if (_netconsoleBar.Visible != visible)
        {
            _netconsoleBar.Visible = visible;
        }
        if (visible)
        {
            UpdateNetconsoleTarget();
            LayoutNetconsoleBar();
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
            if (_netconsoleBar.Visible)
            {
                _netconsoleInput.Focus();
            }
        }
        else
        {
            // What was typed in the box stays there for when line input comes back.
            _serialTerminalControl.Focus();
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
        _netconsoleTargetLabel.ToolTipText = target != null
            ? "Typed input is sent to " + target + " (UDP)."
            : "No datagram has arrived yet. Input goes to the address the first one comes from, or set the device address in the connection settings.";
        LayoutNetconsoleBar();
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
                _netconsoleMenu.Visible = false;
            }
            return;
        }
        if (_netconsoleMenu == null)
        {
            _netconsoleMenu = new ToolStripMenuItem("Netconsole");
            Icons.Apply(_netconsoleMenu, "network");
            _netconsoleLineModeItem = new ToolStripMenuItem("Line input (send each line on Enter)", null,
                (sender, e) => SetNetconsoleInputMode(DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line))
            {
                ToolTipText = "The line is typed in the box below the terminal and sent as one UDP datagram when Enter is pressed."
            };
            _netconsoleCharacterModeItem = new ToolStripMenuItem("Character input (send every key at once)", null,
                (sender, e) => SetNetconsoleInputMode(DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Character))
            {
                ToolTipText = "Every key typed in the terminal is sent at once, as a UDP datagram of its own."
            };
            ToolStripMenuItem sendCtrlC = new ToolStripMenuItem("Send Ctrl+C", null, (sender, e) => SendNetconsoleBytes(new byte[] { 3 }));
            _netconsoleTargetItem = new ToolStripMenuItem { Enabled = false };
            _netconsoleMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _netconsoleLineModeItem, _netconsoleCharacterModeItem, new ToolStripSeparator(), sendCtrlC, new ToolStripSeparator(), _netconsoleTargetItem
            });
            _netconsoleMenu.DropDownOpening += (sender, e) =>
            {
                bool connected = _connectionState == ConnectionStateEnum.Connected;
                sendCtrlC.Enabled = connected;
                string target = DescribeNetconsoleTarget();
                IPEndPoint last = _netconsoleTransport?.LastSender;
                _netconsoleTargetItem.Text = (target != null ? "Input goes to " + target : "Input goes to the first sender (nothing received yet)")
                    + (last != null ? "; last message from " + last.Address : "");
            };
            contextMenuStrip1.Items.Add(_netconsoleMenu);
        }
        _netconsoleMenu.Visible = true;
        ApplyMenuTheme(_netconsoleMenu.DropDown);
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
        _netconsoleDraft = "";
        _netconsoleHistoryIndex = -1;
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
        if (_commandKey != null && _savedHistory.Add(line))
        {
            _commandStorage.SaveHistory(_commandKey, _savedHistory);
            RefreshCommandSuggestions();
        }
    }

    private void OnNetconsoleInputKeyDown(object sender, KeyEventArgs e)
    {
        IReadOnlyList<string> history = _serialTerminalControl.CommandHistory;
        // Below the newest entry while the box holds what is being typed.
        int index = _netconsoleHistoryIndex < 0 || _netconsoleHistoryIndex > history.Count ? history.Count : _netconsoleHistoryIndex;
        switch (e.KeyCode)
        {
            case Keys.Return when !e.Control && !e.Alt:
                SendNetconsoleLine();
                break;
            case Keys.Escape:
                _netconsoleInput.Text = "";
                _netconsoleDraft = "";
                _netconsoleHistoryIndex = -1;
                break;
            case Keys.Up when !e.Control && !e.Alt:
                if (index > 0)
                {
                    if (index == history.Count)
                    {
                        _netconsoleDraft = _netconsoleInput.Text;
                    }
                    index--;
                    _netconsoleHistoryIndex = index;
                    SetNetconsoleInputText(history[index]);
                }
                break;
            case Keys.Down when !e.Control && !e.Alt:
                if (index < history.Count)
                {
                    index++;
                    _netconsoleHistoryIndex = index == history.Count ? -1 : index;
                    SetNetconsoleInputText(index == history.Count ? _netconsoleDraft : history[index]);
                }
                break;
            case Keys.C when e.Control && !e.Alt && !e.Shift && _netconsoleInput.SelectionLength == 0:
                // Nothing to copy: Ctrl+C goes to the device (U-Boot stops a running command with it).
                SendNetconsoleBytes(new byte[] { 3 });
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
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
    private bool RedirectToNetconsoleLine(byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
        if (!NetconsoleLineInput || _netconsoleInput == null || HexMode)
        {
            return false;
        }
        if (inputKind != TerminalEmulatorControl.InputKind.Typed && inputKind != TerminalEmulatorControl.InputKind.Paste)
        {
            return false;
        }
        string text = _textEncoding.GetString(buffer);
        StringBuilder printable = new StringBuilder();
        List<byte> direct = new List<byte>();
        char previous = '\0';
        foreach (char c in text)
        {
            char current = c;
            bool pairedLineFeed = current == '\n' && previous == '\r';
            previous = current;
            if (current == '\r' || current == '\n')
            {
                if (pairedLineFeed)
                {
                    continue; // the CR of the pair has sent the line already
                }
                AppendToNetconsoleInput(printable.ToString());
                printable.Clear();
                SendNetconsoleLine();
            }
            else if (c == '\b' || c == '\u007f')
            {
                if (printable.Length > 0)
                {
                    printable.Length--;
                }
                else if (_netconsoleInput.Text.Length > 0)
                {
                    SetNetconsoleInputText(_netconsoleInput.Text.Substring(0, _netconsoleInput.Text.Length - 1));
                }
            }
            else if (c == '\t' || !char.IsControl(c))
            {
                printable.Append(c);
            }
            else
            {
                direct.AddRange(_textEncoding.GetBytes(c.ToString()));
            }
        }
        AppendToNetconsoleInput(printable.ToString());
        if (direct.Count > 0)
        {
            SendNetconsoleBytes(direct.ToArray());
        }
        if (_netconsoleBar.Visible && !_netconsoleInput.Focused)
        {
            _netconsoleInput.Focus();
            _netconsoleInput.SelectionStart = _netconsoleInput.Text.Length;
            _netconsoleInput.SelectionLength = 0;
        }
        return true;
    }

    private void AppendToNetconsoleInput(string text)
    {
        if (text.Length > 0)
        {
            SetNetconsoleInputText(_netconsoleInput.Text + text);
        }
    }
}
