using SmarTTY.Forms;
using SmarTTY.GUI.Styles;
using SmarTTY.Native;
using SmarTTY.Terminal;
using SmarTTY.TextEditing;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using VisualTTY;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

public partial class DeviceTerminalControl : DpiAwareControl
{

    private ITransportFactory _transportFactory;

    private AsyncTransportConnection _asyncTransportConnection;

    private DeviceTerminalSettings _deviceTerminalSettings;

    // Appearance and key behavior shared with the SSH tabs (Settings window). Everything that is specific to one
    // device (colors, line ending, encoding, baud rate, ...) stays in _deviceTerminalSettings.
    private GlobalSettings _commonSettings;

    private DeviceTerminalSettings.CharacterDisplayMode _displayMode = DeviceTerminalSettings.CharacterDisplayMode.ASCII;

    // Text exchanged with the device (received text, typed and pasted input, echo, "Send" box) always goes
    // through this one encoding, so what is shown and what is sent cannot disagree with each other.
    private Encoding _textEncoding = Encoding.UTF8;

    // Chunks of the stream are decoded with state, so a multi-byte character split between two reads is not corrupted.
    private TextStreamDecoder _textDecoder = new TextStreamDecoder(Encoding.UTF8);

    private bool _updatingEncodingSelection;

    // Active XMODEM/YMODEM/ZMODEM/Kermit byte buffer for interception during file transfers.
    private readonly System.Collections.Concurrent.BlockingCollection<byte> _xmodemRxQueue = new System.Collections.Concurrent.BlockingCollection<byte>();
    // Set on the UI thread before a transfer starts and cleared when it ends; while set, everything received belongs to the
    // protocol and typing, pasting and sending from the toolbar are refused so nothing else gets into the data stream.
    private volatile bool _isXModemActive;

    // The connection that a running transfer belongs to (set together with _isXModemActive). The protocol writes to it and
    // reads only its data: after a reconnect the new session is not part of the transfer - it must neither get the resent
    // packets nor have its output swallowed by the protocol.
    private volatile AsyncTransportConnection _lineOwner;

    // Cancels the running transfer (its window's token); called when the connection is replaced.
    private volatile Action _cancelLineTransfer;

    // Continuous log of received data; null while not recording.
    private TerminalLogWriter _logWriter;

    private int _lastLogButtonUpdate;

    private long _bytesSent;

    private long _bytesReceived;

    private static readonly System.Diagnostics.Stopwatch TrafficClock = System.Diagnostics.Stopwatch.StartNew();

    private readonly TransferRateMeter _sendRate = new TransferRateMeter();

    private readonly TransferRateMeter _receiveRate = new TransferRateMeter();

    // How long a port that was just closed (or just plugged in) is tried again before the connection counts as failed:
    // some USB-serial drivers release or announce a port a moment before it can be opened.
    private const int ReopenRetryMs = 1500;

    private ConnectionStateEnum _connectionState = ConnectionStateEnum.Connecting;

    // Automatic reconnect: when the transport drops or fails and DeviceTerminalSettings.ReconnectAutomatically is
    // set, a fresh connection is opened again, right away or on a timer (ReconnectIntervalSeconds). Only the user's
    // own disconnect cancels it (_manualDisconnect); everything runs on the UI thread (WinForms timer).
    private bool _manualDisconnect;

    private System.Windows.Forms.Timer _autoReconnectTimer;

    // Throttles automatic reconnects: a server that accepts a connection and closes it at once (session limit,
    // device restarting) would otherwise be reconnected in a tight loop, wiping the screen each time.
    private readonly AutoReconnectPolicy _reconnectPolicy = new AutoReconnectPolicy();

    /// <summary>Raised on the UI thread whenever the transport state or the displayed title changes.</summary>
    public event EventHandler StatusChanged;

    /// <summary>Raised when the user switches the display mode in the toolbar, so the profile can store it.</summary>
    public event EventHandler DisplayModeChanged;

    /// <summary>
    /// The text terminal while it has the keyboard focus (null in the HEX and combined views, or when the focus is
    /// in the toolbar), so window-level key handling (F10, Alt+letter) never leaks into the baud rate box or the
    /// "Send" field.
    /// </summary>
    internal SerialTerminalControl FocusedTextTerminal => (!HexMode && _serialTerminalControl.Focused) ? _serialTerminalControl : null;

    /// <summary>
    /// The editor currently shown in the device tab: the decoded text in ASCII mode, or the HEX dump editor in
    /// HEX and combined modes. The shared search bar searches it to find both words and HEX byte sequences.
    /// </summary>
    public TextEditorControl SearchTextEditor => HexMode ? hexTerminalControl1.SearchTextEditor : _serialTerminalControl;

    /// <summary>Raised when the user presses Ctrl+W and the shared "Close tab on Ctrl+W" option is on.</summary>
    public event EventHandler CloseTabRequested;

    /// <summary>Raised when the user presses Alt+Enter (full screen on/off, as in SSH tabs).</summary>
    public event EventHandler FullScreenToggleRequested;

    /// <summary>
    /// Raised when the text terminal opens the command/directory/files completion popup. The controller turns this
    /// into the same suggestion popup that the SSH tabs use.
    /// </summary>
    public event EventHandler<CompletionPopupRequest> CompletionPopupRequested;

    /// <summary>Raised when the user clicks a path or an URL shown in the terminal text (a local file on this machine).</summary>
    public event EventHandler<OpenFileEventArgs> OpenFileRequested;

    /// <summary>
    /// Replays a recorded raw byte log (the bytes, this terminal). The themed WPF window is injected here by
    /// <c>App</c>: this control is compiled by the Mono test harness, which cannot reference the WPF dialogs.
    /// </summary>
    public static Action<byte[], DeviceTerminalControl> LogReplayPrompt;

    /// <summary>
    /// Lets the user change the serial port settings (the settings to edit, or null to keep the driver's; the port name).
    /// Returns whether the dialog was accepted and the chosen settings (null to keep the driver's settings).
    /// </summary>
    public static Func<DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings, string, (bool Accepted, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings Settings)> SerialSettingsPrompt;

    /// <summary>
    /// Creates the progress window of a file transfer (the window title, whether the error count is shown). Injected
    /// by <c>App</c>, as above. Null when no progress window is available: a transfer then runs without one.
    /// </summary>
    public static Func<string, bool, ITransferProgressWindow> TransferProgressWindowFactory;

    /// <summary>
    /// Edits a user command list of the line-input mode (the window title, the explanation, the commands to edit).
    /// Returns the edited commands, or null when the user cancelled. Injected by <c>App</c>, like the hooks above.
    /// </summary>
    public static Func<string, string, IEnumerable<string>, List<string>> CommandListEditor;

    private static ITransferProgressWindow CreateTransferProgressWindow(string title, bool showErrorCount)
    {
        Func<string, bool, ITransferProgressWindow> factory = TransferProgressWindowFactory;
        return factory != null ? factory(title, showErrorCount) : new HeadlessTransferProgressWindow();
    }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Bytes per second sent and received over the last seconds. The rate is measured between calls, so it is meant to
    /// be called regularly on the UI thread (the status bar does it a few times a second).
    /// </summary>
    public void GetTransferRates(out double sendRate, out double receiveRate)
    {
        long now = TrafficClock.ElapsedMilliseconds;
        sendRate = _sendRate.Sample(BytesSent, now);
        receiveRate = _receiveRate.Sample(BytesReceived, now);
    }

    public ConnectionStateEnum ConnectionState => _connectionState;

    public DeviceTerminalSettings Settings => _deviceTerminalSettings;

    /// <summary>Short name for a tab header, e.g. "COM3" or "192.168.0.5:23".</summary>
    public string ConnectionTitle => GetConnectionTitle(_deviceTerminalSettings?.Connection);

    /// <summary>Short name of a connection for a tab header; usable before the control has been given its settings.</summary>
    public static string GetConnectionTitle(DeviceTerminalSettings.ConnectionSettings connection)
    {
        return connection switch
        {
            DeviceTerminalSettings.ConnectionSettings.Serial serial => serial.ComPortName,
            DeviceTerminalSettings.ConnectionSettings.Tcp tcp => tcp.ToString(),
            _ => "Raw Terminal",
        };
    }

    /// <summary>
    /// One-line description for the status bar, e.g. "COM3 (115200 bps): connected". While the connection is up it
    /// also says what the terminal believes the device is doing (at its prompt, or running a full-screen program),
    /// which is what explains why the keys are edited here or sent to the device.
    /// </summary>
    public string StatusText
    {
        get
        {
            string name = _transportDisplayName ?? ConnectionTitle;
            return _connectionState switch
            {
                ConnectionStateEnum.Connecting => name + ": connecting...",
                ConnectionStateEnum.Connected => ConnectedStatusText(name),
                ConnectionStateEnum.Failed => name + ": connection failed",
                _ => name + ": disconnected",
            };
        }
    }

    private string ConnectedStatusText(string name)
    {
        string state = DeviceTerminalStateText.State(connected: true, _serialTerminalControl.HasInputLine, _serialTerminalControl.FullScreenProgramActive, _serialTerminalControl.CurrentPrompt);
        return state.Length == 0 ? name + ": connected" : name + ": connected (" + state + ")";
    }

    private string _transportDisplayName;

    // What the status line last reported, so a change is announced only when it happens (the status bar reads the
    // text a few times a second, and a repaint per tick would be wasted work).
    private string _reportedStatusText;

    private void RaiseStatusChanged()
    {
        _reportedStatusText = StatusText;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Announces a status change when the state the status line reports is not the one it reported last. The
    /// COM/TCP terminal calls this when it shows or hides its input line or a full-screen program takes over.
    /// </summary>
    internal void RefreshStatusIfChanged()
    {
        if (!string.Equals(_reportedStatusText, StatusText, StringComparison.Ordinal))
        {
            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// How the received bytes are shown: the decoded text (<see cref="DeviceTerminalSettings.CharacterDisplayMode.ASCII"/>),
    /// the HEX dump alone, or the HEX dump with the text pane next to it. The toolbar and the connection profile
    /// both drive this one property, so switching in the toolbar is not limited to the current session.
    /// </summary>
    public DeviceTerminalSettings.CharacterDisplayMode DisplayMode
    {
        get
        {
            return _displayMode;
        }
        set
        {
            ApplyDisplayMode(value, raiseChangedEvent: true);
        }
    }

    /// <summary>True when the HEX control is on screen, alone or together with the text pane.</summary>
    public bool HexMode => _displayMode != DeviceTerminalSettings.CharacterDisplayMode.ASCII;

    private void ApplyDisplayMode(DeviceTerminalSettings.CharacterDisplayMode mode, bool raiseChangedEvent)
    {
        _displayMode = mode;
        bool hexView = mode != DeviceTerminalSettings.CharacterDisplayMode.ASCII;
        bool showTextView = mode == DeviceTerminalSettings.CharacterDisplayMode.Both;
        hexTerminalControl1.Visible = hexView;
        _serialTerminalControl.Visible = !hexView;
        cbASCII.Checked = mode == DeviceTerminalSettings.CharacterDisplayMode.ASCII;
        cbHEX.Checked = mode == DeviceTerminalSettings.CharacterDisplayMode.HEX;
        cbBoth.Checked = mode == DeviceTerminalSettings.CharacterDisplayMode.Both;
        ToolStripLabel toolStripLabel = lblSend;
        ToolStripTextBox toolStripTextBox = txtHexInput;
        bool isVisible = (btnSend.Visible = hexView);
        bool visible = (toolStripTextBox.Visible = isVisible);
        toolStripLabel.Visible = visible;

        if (_deviceTerminalSettings != null)
        {
            _deviceTerminalSettings.DisplayMode = mode;
        }
        DeviceTerminalSettings.HexTerminalSettings hexSettings = _deviceTerminalSettings?.HexSettings;
        if (hexSettings == null)
        {
            // Only before the first ApplySettings: keep the control usable without a profile object.
            hexSettings = new DeviceTerminalSettings.HexTerminalSettings
            {
                MaximumBytesPerLine = 16,
                BreaksAroundEcho = true,
                AutoSend = true,
                SendAsHex = true
            };
        }
        hexSettings.ShowTextView = showTextView;
        hexTerminalControl1.HexSettings = hexSettings;

        if (raiseChangedEvent)
        {
            DisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private SerialTerminalControl _serialTerminalControl;

    // The menu items of the designer have no images in the designer file: they are given here (the check items
    // show their state with a check mark instead).
    private void ApplyMenuIcons()
    {
        foreach (ToolStripMenuItem item in new[]
        {
            sendBinaryFileToolStripMenuItem, sendFileViaXMODEMToolStripMenuItem, sendFileViaXMODEM1KToolStripMenuItem,
            sendFilesViaYMODEMToolStripMenuItem, sendFilesViaZModemToolStripMenuItem, sendFilesViaKermitToolStripMenuItem
        })
        {
            Icons.Apply(item, "upload");
        }
        foreach (ToolStripMenuItem item in new[]
        {
            receiveFileViaXMODEMToolStripMenuItem, receiveFilesViaYMODEMToolStripMenuItem, receiveFilesViaYMODEMGToolStripMenuItem,
            receiveFilesViaZModemToolStripMenuItem, receiveFilesViaKermitToolStripMenuItem
        })
        {
            Icons.Apply(item, "download");
        }
        Icons.Apply(terminalTypeToolStripMenuItem, "terminal");
        Icons.Apply(changeSettingsToolStripMenuItem, "settings");
    }

    public DeviceTerminalControl()
    {
        InitializeComponent();
        ApplyMenuIcons();
        DpiAwareForm.ConfigureToolStrip(toolStrip1);
        Disposed += DeviceTerminalControl_Disposed;
        pnlConnectionInProgress.BringToFront();
        toolStrip1.SendToBack();
        
        // Replace the designer-created TerminalEmulatorControl with our SerialTerminalControl
        ReplaceTerminalControl();
        // A telnet server may ask for the terminal size (and wants to hear about changes); other transports ignore it.
        _serialTerminalControl.TerminalSizeChanged += OnTerminalSizeChanged;
        AddClearCommand();
        AddReturnToInputLineCommand();
        AddPasteCommands();
        AddPasteSpecialCommands();
        AddRemoteSizeCommands();
        AddPromptDetectionCommand();
        InitializeCommandLists();
        
        foreach (TerminalEncodings.Choice choice in TerminalEncodings.All)
        {
            cbEncoding.Items.Add(choice);
        }
        SelectEncodingInToolbar(TerminalEncodings.DefaultName);
        InitializeSerialLineControls();
        InitializeZModemMenu();
        InitializeYModemMenu();
        InitializeKermitMenu();
        InitializeGzipBase64Menu();
        InitializeTftpSupport();
        SerialPortArrivalWatcher.Subscribe(OnSerialPortArrived);
        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyToolbarTheme();
        // Some menus get their items later (the Telnet menu, for instance): they are themed again when they open.
        contextMenuStrip1.Opening += (sender, e) => ApplyMenuTheme(contextMenuStrip1);
        foreach (ToolStripItem item in toolStrip1.Items)
        {
            if (item is ToolStripDropDownItem dropDownItem)
            {
                dropDownItem.DropDownOpening += (sender, e) => ApplyMenuTheme(dropDownItem.DropDown);
            }
        }
    }

    private ToolbarPalette _toolbarPalette;

    private ToolStripRenderer _toolbarRenderer;

    private void OnThemeChanged(object sender, EventArgs e)
    {
        ApplyToolbarTheme();
    }

    /// <summary>
    /// Paints the tool bar, its menus and the terminal context menu in the colours of the application theme.
    /// </summary>
    private void ApplyToolbarTheme()
    {
        _toolbarPalette = ToolbarPalette.FromCurrentTheme();
        _toolbarRenderer = new ThemedToolStripRenderer(_toolbarPalette);
        toolStrip1.Renderer = _toolbarRenderer;
        toolStrip1.BackColor = _toolbarPalette.Background;
        toolStrip1.ForeColor = _toolbarPalette.Text;
        foreach (ToolStripItem item in toolStrip1.Items)
        {
            switch (item)
            {
                case ToolStripComboBox comboBox:
                    comboBox.ComboBox.FlatStyle = FlatStyle.Flat;
                    comboBox.BackColor = _toolbarPalette.Input;
                    comboBox.ForeColor = _toolbarPalette.Text;
                    break;
                case ToolStripTextBox textBox:
                    textBox.BackColor = _toolbarPalette.Input;
                    textBox.ForeColor = _toolbarPalette.Text;
                    break;
                case ToolStripDropDownItem dropDownItem:
                    dropDownItem.ForeColor = _toolbarPalette.Text;
                    ApplyMenuTheme(dropDownItem.DropDown);
                    break;
                default:
                    item.ForeColor = _toolbarPalette.Text;
                    break;
            }
        }
        // The port alert is a link and "Stop log" is shown while recording: both use the error colour.
        if (_lblPortAlert != null)
        {
            _lblPortAlert.LinkColor = _lblPortAlert.ActiveLinkColor = _lblPortAlert.VisitedLinkColor = _toolbarPalette.Error;
        }
        if (_logWriter != null)
        {
            btnLog.ForeColor = _toolbarPalette.Error;
        }
        ApplyMenuTheme(contextMenuStrip1);
        toolStrip1.Invalidate();
    }

    private void ApplyMenuTheme(ToolStripDropDown menu)
    {
        menu.Renderer = _toolbarRenderer;
        menu.BackColor = _toolbarPalette.Popup;
        menu.ForeColor = _toolbarPalette.Text;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = _toolbarPalette.Text;
            if (item is ToolStripDropDownItem { HasDropDownItems: true } subMenu)
            {
                ApplyMenuTheme(subMenu.DropDown);
            }
        }
    }

    // The Clear button of the tool bar, also in the context menu of the terminal.
    private void AddClearCommand()
    {
        contextMenuStrip1.Items.Add(new ToolStripMenuItem("Clear", toolStripButton1.Image, (sender, args) => ClearOutput()));
    }

    // While a command runs, the keys go to the device as they are (mc, htop, vi ...) and the local input line is
    // hidden. The terminal brings the line back by itself when the program leaves; this command is for the cases
    // when it cannot tell: a program that was killed without restoring the terminal, or a prompt of an unusual shape.
    private ToolStripMenuItem _returnToInputLineMenuItem;

    private void AddReturnToInputLineCommand()
    {
        _returnToInputLineMenuItem = new ToolStripMenuItem("Return to input line", null, (sender, args) => _serialTerminalControl.ReturnToInputLine())
        {
            ToolTipText = "Show the input line again if the keys keep going to a program that has already ended (mc, htop ...)"
        };
        Icons.Apply(_returnToInputLineMenuItem, "go");
        contextMenuStrip1.Items.Add(_returnToInputLineMenuItem);
    }

    // Paste in other ways: as one line, and slowly (small pieces with pauses, waiting for the echo or the prompt).
    private void AddPasteSpecialCommands()
    {
        ToolStripMenuItem menu = new ToolStripMenuItem("Paste special");
        menu.DropDownItems.Add(new ToolStripMenuItem("Paste as one line (lines joined with a space)", null, (sender, args) => PasteSpecial(PasteMode.OneLineSpaces)));
        menu.DropDownItems.Add(new ToolStripMenuItem("Paste as one line (lines joined with &&)", null, (sender, args) => PasteSpecial(PasteMode.OneLineAnd)));
        menu.DropDownItems.Add(new ToolStripMenuItem("Paste slowly", null, (sender, args) => PasteSpecial(PasteMode.Slow))
        {
            ToolTipText = "Sends the text in small pieces with pauses, and can wait for the echo or the prompt of the device (Settings > Paste), so that a slow device does not lose characters."
        });
        contextMenuStrip1.Items.Add(menu);
    }

    /// <summary>Pastes the clipboard text as one line or slowly (see <see cref="PasteMode"/>).</summary>
    public void PasteSpecial(PasteMode mode)
    {
        _pasteMode = mode;
        try
        {
            pasteToolStripMenuItem_Click(this, EventArgs.Empty);
        }
        finally
        {
            _pasteMode = PasteMode.Normal;
        }
    }

    private PasteMode _pasteMode;

    private PasteJob _pasteJob;

    // From this size a paste is sent in pieces, one after another, so that the window does not wait for the whole text.
    private const int AsyncPasteThreshold = 16 * 1024;

    private const int AsyncPasteChunkSize = 4096;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        PasteJob job = _pasteJob;
        if (keyData == Keys.Escape && job != null && job.IsActive)
        {
            job.Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // A serial line and a raw TCP connection cannot carry the size of the window, so mc, htop and the like draw into
    // the default 80x24 corner of it. For a Linux shell on the device the size is set with COLUMNS/LINES and stty: "Send terminal
    // size now" does it once, "Keep ... in sync" puts the command in front of the next command whenever the window
    // has another size than the one the device was told last. Telnet passes the size on by itself (NAWS), so
    // these commands are not shown for it. A program that is already running cannot be told: the new size is
    // applied by the next command typed in the input line.
    private ToolStripMenuItem _syncRemoteSizeMenuItem;

    private ToolStripMenuItem _sendRemoteSizeMenuItem;

    private void AddRemoteSizeCommands()
    {
        _syncRemoteSizeMenuItem = new ToolStripMenuItem("Keep device terminal size in sync", null, (sender, args) => ToggleSyncRemoteSize())
        {
            ToolTipText = "For a Linux shell on the device: tell it the size of this window (COLUMNS, LINES, stty) before the next command, so mc, htop and the like fill the window. It acts only after the device has shown a shell prompt (\"# \" or \"$ \"); switch it off for a console that uses such a prompt but is not a Linux shell."
        };
        _sendRemoteSizeMenuItem = new ToolStripMenuItem("Send terminal size now", null, (sender, args) => _serialTerminalControl.SendRemoteSizeNow())
        {
            ToolTipText = "Types \"export COLUMNS=N LINES=M; stty cols N rows M\" for the shell on the device (available at the input line when nothing is typed in it)."
        };
        Icons.Apply(_sendRemoteSizeMenuItem, "refresh");
        contextMenuStrip1.Items.Add(_syncRemoteSizeMenuItem);
        contextMenuStrip1.Items.Add(_sendRemoteSizeMenuItem);
    }

    private void ToggleSyncRemoteSize()
    {
        bool sync = !_syncRemoteSizeMenuItem.Checked;
        _syncRemoteSizeMenuItem.Checked = sync;
        _serialTerminalControl.SyncRemoteSize = sync;
        if (_deviceTerminalSettings != null)
        {
            _deviceTerminalSettings.SyncRemoteTerminalSize = sync;
        }
    }

    // The runtime counterpart of the "Detect the prompt of the device" option in the connection settings: a device
    // that prints something that only looks like a shell prompt makes the input line continue it, and the setting is
    // buried in a dialog. Turning it off here keeps the configured prompt. Like the other runtime toggles of this
    // menu it updates the profile object of the open tab; saving the profile stores the new value.
    private ToolStripMenuItem _autoDetectPromptMenuItem;

    private void AddPromptDetectionCommand()
    {
        _autoDetectPromptMenuItem = new ToolStripMenuItem("Detect the prompt of the device", null, (sender, args) => ToggleAutoDetectPrompt())
        {
            ToolTipText = "Continue a prompt the device prints (\"# \", \"$ \", \"> \") in the input line. Switch it off when the device prints something that only looks like a prompt: the input line then always uses the configured prompt."
        };
        contextMenuStrip1.Items.Add(_autoDetectPromptMenuItem);
    }

    private void ToggleAutoDetectPrompt()
    {
        bool detect = !_autoDetectPromptMenuItem.Checked;
        _autoDetectPromptMenuItem.Checked = detect;
        _serialTerminalControl.EnableAutoPromptDetection(detect);
        if (_deviceTerminalSettings != null)
        {
            _deviceTerminalSettings.AutoDetectPrompt = detect;
        }
    }

    private void OnTerminalSizeChanged(TerminalEmulatorControl terminal, int columns, int rows)
    {
        _asyncTransportConnection?.SetTerminalSize(columns, rows);
    }

    // --- telnet: local echo that follows the server, and the "Telnet" commands in the context menu ---

    private TelnetTransport _telnetTransport;

    private ToolStripMenuItem _telnetMenu;

    // Whether the terminal shows what is typed itself (while no input line is shown; the input line shows its text
    // anyway). Not the terminal's LocalEcho property: in SerialTerminalControl that one sends every typed character
    // to the device at once, on top of the command that is sent when Enter is pressed.
    private bool _telnetLocalEcho;

    private void SetTelnetLocalEcho(bool echo)
    {
        _telnetLocalEcho = echo;
        _serialTerminalControl.EchoRawInput = echo;
        echoTypedCharactersToolStripMenuItem.Checked = echo;
    }

    // Called for every new telnet connection (also when it is reconnected): applies the local echo mode and, in
    // the automatic mode, keeps the echo opposite to the server's.
    private void AttachTelnet(TelnetTransport transport, DeviceTerminalSettings.ConnectionSettings.Telnet settings)
    {
        _telnetTransport = transport;
        transport.RemoteStatusReceived += (sender, args) => OnTelnetStatusReceived(transport);
        _serialTerminalControl.LocalEcho = false;
        switch (settings.LocalEcho)
        {
            case DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Always:
                SetTelnetLocalEcho(true);
                break;
            case DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Never:
                SetTelnetLocalEcho(false);
                break;
            default:
                // Nobody echoes until the server says it does.
                SetTelnetLocalEcho(true);
                transport.RemoteEchoChanged += (sender, args) => OnTelnetRemoteEchoChanged(transport);
                break;
        }
    }

    private void OnTelnetRemoteEchoChanged(TelnetTransport transport)
    {
        // Raised on the reading thread.
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                // A reconnect creates a new transport; the news of an old one is of no interest.
                if (!IsDisposed && ReferenceEquals(transport, _telnetTransport))
                {
                    SetTelnetLocalEcho(!transport.RemoteEcho);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void UpdateTelnetMenu(bool telnet)
    {
        if (!telnet)
        {
            if (_telnetMenu != null)
            {
                _telnetMenu.Visible = false;
            }
            return;
        }
        if (_telnetMenu == null)
        {
            _telnetMenu = new ToolStripMenuItem("Telnet");
            Icons.Apply(_telnetMenu, "network");
            AddTelnetCommand("Send Break", TelnetStream.Break);
            AddTelnetCommand("Send Interrupt Process (IP)", TelnetStream.InterruptProcess);
            AddTelnetCommand("Send Are You There (AYT)", TelnetStream.AreYouThere);
            AddTelnetCommand("Send Erase Line", TelnetStream.EraseLine);
            AddTelnetCommand("Send NOP", TelnetStream.NOP);
            _telnetMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem showOptions = new ToolStripMenuItem("Show negotiated options", null, (sender, args) => ShowTelnetOptions());
            Icons.Apply(showOptions, "question");
            _telnetMenu.DropDownItems.Add(showOptions);
            ToolStripMenuItem askStatus = new ToolStripMenuItem("Ask server for its option status (STATUS)", null, (sender, args) => RequestTelnetStatus());
            Icons.Apply(askStatus, "refresh");
            _telnetMenu.DropDownItems.Add(askStatus);
            _telnetMenu.DropDownOpening += (sender, args) =>
            {
                bool connected = _connectionState == ConnectionStateEnum.Connected && _telnetTransport != null;
                foreach (ToolStripItem item in _telnetMenu.DropDownItems)
                {
                    item.Enabled = connected;
                }
            };
            contextMenuStrip1.Items.Add(_telnetMenu);
        }
        _telnetMenu.Visible = true;
    }

    private void ShowTelnetOptions()
    {
        TelnetTransport transport = _telnetTransport;
        if (transport == null)
        {
            return;
        }
        ThemedMessageBox.Show(transport.DescribeOptions(), "Telnet options", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RequestTelnetStatus()
    {
        TelnetTransport transport = _telnetTransport;
        if (transport == null)
        {
            return;
        }
        try
        {
            if (!transport.RequestRemoteStatus())
            {
                ThemedMessageBox.Show("The server did not offer to report its option status (the STATUS option).", "Telnet STATUS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception)
        {
            // The connection is gone; the status line says so.
        }
    }

    private void OnTelnetStatusReceived(TelnetTransport transport)
    {
        // Raised on the reading thread.
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && ReferenceEquals(transport, _telnetTransport))
                {
                    ThemedMessageBox.Show("The server reports these options:\r\n\r\n" + transport.RemoteStatus, "Telnet STATUS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void AddTelnetCommand(string text, byte command)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text, null, (sender, args) =>
        {
            try
            {
                _telnetTransport?.SendCommand(command);
            }
            catch (Exception)
            {
                // The connection is gone; the status line says so.
            }
        });
        Icons.Apply(item, "upload");
        _telnetMenu.DropDownItems.Add(item);
    }

    private void ReplaceTerminalControl()
    {
        // Remove the old control
        if (terminalEmulatorControl1 != null)
        {
            terminalEmulatorControl1.DataEntered -= OnTerminalDataEntered;
            terminalEmulatorControl1.KeyDown -= OnTerminalKeyDown;
            Controls.Remove(terminalEmulatorControl1);
            terminalEmulatorControl1.Dispose();
        }

        // Create and configure the new SerialTerminalControl
        _serialTerminalControl = new SerialTerminalControl
        {
            ActiveBufferIndex = 0,
            AutoScroll = true,
            AutoWrap = true,
            BackColor = Color.Black,
            BellMode = TerminalEmulatorControl.BeepMode.None,
            BellSoundFile = null,
            NoLeftPadding = false,
            NormalTextColor = Color.White,
            CaptureDialogKeys = false,
            CaretBounds = new Rectangle(0, 0, 0, 0),
            TerminalMode = true,
            ContextMenuStrip = contextMenuStrip1,
            TabWidth = 8,
            Dock = DockStyle.Fill,
            LayoutSuspended = false,
            Font = new Font("Courier New", 12f, FontStyle.Regular, GraphicsUnit.Point, 204),
            ForeColor = Color.LightGray,
            NavigationLocked = true,
            NoHorizontalScroll = false,
            HideCaret = false,
            HideMargins = true,
            HorizontalScrollBarVisible = false,
            ForwardAltF4 = false,
            MaxChunkLength = 0,
            ShowLineNumbers = true,
            TopLine = 0,
            Location = new Point(0, 0),
            SelectionStyle = new HighlightStyle(),
            Margin = new Padding(5, 4, 5, 4),
            UnusedValue = 0,
            MouseMode = TerminalEmulatorControl.MouseReportingMode.Off,
            Name = "serialTerminalControl1",
            EnterSequence = null,
            OverwriteMode = false,
            ReadOnly = true,
            CaretWhenUnfocused = false,
            ShowUnrecognizedAsHex = false,
            SingleLine = false,
            Size = new Size(634, 236),
            SmartAutoScroll = false,
            TabIndex = 0,
            TabSize = 4,
            ScrollbackLimit = int.MaxValue,
            Title = null,
            GroupBinaryDigits = false,
            IsModified = false,
            VerticalScrollBarVisible = true,
            Dispatcher = null
        };

        // Hook up events
        _serialTerminalControl.DataEntered += OnTerminalDataEntered;
        _serialTerminalControl.KeyDown += OnTerminalKeyDown;
        _serialTerminalControl.CompletionPopupRequested += OnSerialCompletionPopupRequested;
        _serialTerminalControl.OpenFileRequested += OnSerialOpenFileRequested;
        // The status line shows what the terminal is doing (at its prompt, a full-screen program), so it has to be
        // told when that changes.
        _serialTerminalControl.InputLineStateChanged += (sender, args) => RefreshStatusIfChanged();

        // Add to controls at the same position as the old control
        Controls.Add(_serialTerminalControl);
        Controls.SetChildIndex(_serialTerminalControl, Controls.GetChildIndex(hexTerminalControl1));
    }

    private void SetTextEncoding(string encodingName)
    {
        _textEncoding = TerminalEncodings.Resolve(encodingName);
        _textDecoder = new TextStreamDecoder(_textEncoding);
        _serialTerminalControl.InputEncoding = _textEncoding;
        // A recording in progress follows the change, so a text log matches what the terminal shows.
        _logWriter?.SetEncoding(_textEncoding);
        ApplyByteOrientedLineEditing();
    }

    // Byte mode (DeviceTerminalSettings.ByteOrientedLineEditing) only means something with UTF-8: in a single-byte
    // code page every letter is one byte, as the device's shell counts them.
    private void ApplyByteOrientedLineEditing()
    {
        bool byteMode = (_deviceTerminalSettings?.ByteOrientedLineEditing ?? false) && _textEncoding.CodePage == Encoding.UTF8.CodePage;
        _serialTerminalControl.ByteOrientedLineEditing = byteMode;
        _textDecoder.EscapeInvalidBytes = byteMode;
    }

    private void SelectEncodingInToolbar(string encodingName)
    {
        _updatingEncodingSelection = true;
        try
        {
            cbEncoding.SelectedItem = null;
            foreach (TerminalEncodings.Choice choice in TerminalEncodings.All)
            {
                if (string.Equals(choice.Name, encodingName, StringComparison.OrdinalIgnoreCase))
                {
                    cbEncoding.SelectedItem = choice;
                    break;
                }
            }
        }
        finally
        {
            _updatingEncodingSelection = false;
        }
    }

    private void cbEncoding_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_updatingEncodingSelection || _deviceTerminalSettings == null)
        {
            return;
        }
        if (cbEncoding.SelectedItem is TerminalEncodings.Choice choice)
        {
            // Applies to data received from now on; text already on the screen is not re-decoded.
            _deviceTerminalSettings.TextEncoding = choice.Name;
            SetTextEncoding(choice.Name);
        }
    }

    private void DeviceTerminalControl_Disposed(object sender, EventArgs e)
    {
        _pasteJob?.Abort();
        // The watcher and the theme event are static: a closed tab must not stay reachable from them.
        SerialPortArrivalWatcher.Unsubscribe(OnSerialPortArrived);
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _manualDisconnect = true;
        StopAutoReconnectTimer();
        _autoReconnectTimer?.Dispose();
        _autoReconnectTimer = null;
        _logWriter?.Dispose();
        _logWriter = null;
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Dispose();
        }
    }

    private void OnSerialCompletionPopupRequested(object sender, CompletionPopupRequest e)
    {
        CompletionPopupRequested?.Invoke(this, e);
    }

    private void OnSerialOpenFileRequested(object sender, OpenFileEventArgs e)
    {
        OpenFileRequested?.Invoke(this, e);
    }

    private static string GetLineEnding(DeviceTerminalSettings.LineEndingType lineEndingType)
    {
        return lineEndingType switch
        {
            DeviceTerminalSettings.LineEndingType.LF => "\n",
            DeviceTerminalSettings.LineEndingType.CR => "\r",
            DeviceTerminalSettings.LineEndingType.CRLF => "\r\n",
            _ => null,
        };
    }

    internal static CallbackTransportFactory.TransportCallback CreateTransportCallback(DeviceTerminalSettings.ConnectionSettings connectionSettings, string enterSequence = null)
    {
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Serial)
        {
            return () => new SerialTransport(connectionSettings as DeviceTerminalSettings.ConnectionSettings.Serial);
        }
        // Telnet derives from Tcp, so it has to be recognized first.
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
        {
            return () => new TelnetTransport(telnet, enterSequence);
        }
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Tcp)
        {
            return () => new TcpTransport(connectionSettings as DeviceTerminalSettings.ConnectionSettings.Tcp);
        }
        return null;
    }

    /// <summary>
    /// Applies the settings that SSH tabs share through the Settings window: font, ANSI palette and the key and
    /// mouse behavior (Shift+PageUp/Down, mouse wheel, Alt+letter, Ctrl-C/V, Ctrl+W, copy on selection).
    /// Per-device values (background/text/echo/disconnected colors, line ending, encoding, ...) are not touched,
    /// so this can be called again at any time, e.g. after the user changes the shared settings.
    /// </summary>
    public void ApplyCommonSettings(GlobalSettings settings)
    {
        if (settings == null)
        {
            return;
        }
        _commonSettings = settings;
        SerialTerminalControl terminal = _serialTerminalControl;
        terminal.AppSettings = settings;
        terminal.CopyOnSelect = settings.CopyOnSelection;
        hexTerminalControl1.CopyOnSelect = settings.CopyOnSelection;
        terminal.SetPalette(settings.PaletteOverride ?? ConsolePalette.PuTTY);
        try
        {
            // Same rule as for SSH tabs: without a configured font the control keeps its own default.
            Font configuredFont = settings.ConfiguredFont;
            if (configuredFont != null)
            {
                hexTerminalControl1.Font = configuredFont;
                terminal.Font = configuredFont;
            }
        }
        catch
        {
            // An uninstalled font must not break the terminal; the previous font stays.
        }
        if (terminal.IsHandleCreated)
        {
            terminal.UpdateLayout();
            terminal.Redraw();
            hexTerminalControl1.Invalidate(true);
        }
    }

    public void ApplySettings(DeviceTerminalSettings deviceTerminalSettings)
    {
        _deviceTerminalSettings = deviceTerminalSettings;
        // The automatic login of a telnet connection ends its lines like the Enter key does (a bare CR when none is set).
        CallbackTransportFactory.TransportCallback callback = CreateTransportCallback(deviceTerminalSettings.Connection, GetLineEnding(deviceTerminalSettings.LineEnding) ?? "\r");
        if (callback != null)
        {
            _transportFactory = new CallbackTransportFactory(callback);
        }
        ToolStripLabel toolStripLabel = lblBaudRate;
        bool visible = (cbBaudRate.Visible = deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial);
        toolStripLabel.Visible = visible;
        if (deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial { AdvancedSettings: { } advancedSettings })
        {
            cbBaudRate.Text = advancedSettings.BaudRate.ToString();
        }
        SerialTerminalControl terminal = _serialTerminalControl;
        Color backColor = (hexTerminalControl1.BackColor = deviceTerminalSettings.Colors.Background);
        terminal.BackColor = backColor;
        backColor = (hexTerminalControl1.ForeColor = deviceTerminalSettings.Colors.Text);
        terminal.ForeColor = backColor;
        _serialTerminalControl.EnterSequence = GetLineEnding(deviceTerminalSettings.LineEnding);
        // The prompt drawn before the device prints one of its own (and when none is recognised).
        _serialTerminalControl.SetPrompt(deviceTerminalSettings.DefaultPrompt, deviceTerminalSettings.AutoDetectPrompt);
        SetTextEncoding(deviceTerminalSettings.TextEncoding);
        SelectEncodingInToolbar(deviceTerminalSettings.TextEncoding);
        hexTerminalControl1.EchoBrush = new SolidBrush(_deviceTerminalSettings.Colors.Echo);
        // One persisted mode decides what is rendered; older profiles that enabled the text pane next to the HEX
        // dump are resolved to the combined view instead of silently dropping one of the two panes.
        DeviceTerminalSettings.CharacterDisplayMode displayMode = DeviceTerminalSettings.CharacterDisplayModeHelper.Resolve(
            _deviceTerminalSettings.DisplayMode, _deviceTerminalSettings.HexSettings);
        ApplyDisplayMode(displayMode, raiseChangedEvent: false);
        echoTypedCharactersToolStripMenuItem.Checked = _deviceTerminalSettings.EchoTypedCharacters;
        _serialTerminalControl.LocalEcho = _deviceTerminalSettings.EchoTypedCharacters;
        if (deviceTerminalSettings.HexSettings != null)
        {
            sendAsHEXToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.SendAsHex;
            sendImmediatelyToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.AutoSend;
        }
        changeSettingsToolStripMenuItem.Visible = _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial;
        bool needsStty = !(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet);
        _syncRemoteSizeMenuItem.Visible = needsStty;
        _sendRemoteSizeMenuItem.Visible = needsStty;
        _syncRemoteSizeMenuItem.Checked = needsStty && _deviceTerminalSettings.SyncRemoteTerminalSize;
        _serialTerminalControl.SyncRemoteSize = _syncRemoteSizeMenuItem.Checked;
        _autoDetectPromptMenuItem.Checked = _deviceTerminalSettings.AutoDetectPrompt;
        UpdateSerialLineUi();
        UpdateTelnetMenu(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet);
        ConfigureCommandLists();
        Connect();
    }

    private void Connect(bool resetTerminal = true, int value = 0, bool automatic = false)
    {
        // Any explicit connect (reconnect button, auto-reconnect, settings change) cancels a pending auto-reconnect
        // and is not treated as a manual disconnect anymore.
        _manualDisconnect = false;
        StopAutoReconnectTimer();
        if (!automatic)
        {
            // The user (re)connected by hand: earlier failed automatic attempts no longer count.
            _reconnectPolicy.Reset();
        }
        int connectTimeoutMs = value;
        // A transfer belongs to the connection that is being replaced: stop it instead of letting it wait for retries that
        // would go to the new session.
        _cancelLineTransfer?.Invoke();
        bool replacesConnection = _asyncTransportConnection != null;
        if (replacesConnection)
        {
            _asyncTransportConnection.Dispose();
        }
        _asyncTransportConnection = null;
        if (resetTerminal)
        {
            _serialTerminalControl.Reset();
        }
        // A half-received character from the previous connection must not be glued to the new data.
        _textDecoder.Reset();
        _logWriter?.ResetDecoder();
        IDeviceTransport deviceTransport = this._transportFactory.CreateTransport();
        if (replacesConnection && deviceTransport is SerialTransport)
        {
            // Closing waits for the port to be released; if the driver still holds it for a moment, the new connection
            // retries on its own thread instead of the UI thread sleeping here. A network connection is not retried
            // this way: a refusal is final for this attempt, and the reconnect policy decides when to try again.
            connectTimeoutMs = Math.Max(connectTimeoutMs, ReopenRetryMs);
        }
        _transportDisplayName = deviceTransport.GetDisplayName(includeParameters: true);
        lblConnection.Text = "Connected to " + _transportDisplayName;
        Interlocked.Exchange(ref _bytesSent, 0L);
        Interlocked.Exchange(ref _bytesReceived, 0L);
        _connectionState = ConnectionStateEnum.Connecting;
        _telnetTransport = null;
        if (deviceTransport is TelnetTransport telnetTransport && _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet telnetSettings)
        {
            // The server may propose a character set (option CHARSET): the terminal's own one is accepted.
            telnetTransport.Charset = _textEncoding?.WebName;
            AttachTelnet(telnetTransport, telnetSettings);
        }
        _asyncTransportConnection = new AsyncTransportConnection(deviceTransport, Dispatcher.CurrentDispatcher, connectTimeoutMs);
        _asyncTransportConnection.DataReceived += OnDataReceived;
        _asyncTransportConnection.StateChanged += OnConnectionStateChanged;
        _asyncTransportConnection.WriterIdleChanged += OnWriterIdleChanged;
        _asyncTransportConnection.WriteFailed += OnWriteFailed;
        // Notices belong to the previous connection.
        _portProblem = null;
        _portProblemIsWrite = false;
        _lineErrors.Reset();
        UpdatePortAlert();
        Size terminalSize = _serialTerminalControl.TerminalSize;
        _asyncTransportConnection.SetTerminalSize(terminalSize.Width, terminalSize.Height);
        _asyncTransportConnection.Start();
    }

    private void OnWriterIdleChanged(object sender, EventArgs e)
    {
    }

    /// <summary>
    /// Queues data for the device; false when the connection has already ended and the data was dropped (and
    /// <paramref name="completed"/> will not be called). Otherwise <paramref name="completed"/> is called once, on a
    /// worker thread, when the data has been written or dropped.
    /// </summary>
    private bool SendToDevice(byte[] buffer, int count, Action<bool> completed = null)
    {
        return SendToConnection(_asyncTransportConnection, buffer, count, completed);
    }

    /// <summary>As <see cref="SendToDevice"/>, but to the given connection: a transfer must not follow a reconnect to the new session.</summary>
    private bool SendToConnection(AsyncTransportConnection connection, byte[] buffer, int count, Action<bool> completed = null)
    {
        if (connection == null)
        {
            throw new InvalidOperationException("The connection is closed. Use Reconnect first.");
        }
        if (!ReferenceEquals(connection, _asyncTransportConnection))
        {
            // The connection was replaced (reconnect): the data belongs to a session that no longer exists.
            return false;
        }
        // A connection that has already ended drops the data; the toolbar says it is closed.
        if (!connection.Send(buffer, count, completed))
        {
            return false;
        }
        Interlocked.Add(ref _bytesSent, count);
        return true;
    }

    /// <summary>Reopens the connection when it is closed or failed; an established connection is left alone.</summary>
    public void ReconnectIfClosed()
    {
        if (_transportFactory != null && (_connectionState == ConnectionStateEnum.Disconnected || _connectionState == ConnectionStateEnum.Failed))
        {
            Connect();
        }
    }

    /// <summary>True while an automatic reconnect can actually open a new connection (the user has not disconnected manually in the meantime).</summary>
    private bool CanAutoReconnect =>
        !_manualDisconnect &&
        _transportFactory != null &&
        (_connectionState == ConnectionStateEnum.Disconnected || _connectionState == ConnectionStateEnum.Failed);

    /// <summary>
    /// Arranges the next automatic reconnect after the transport dropped or failed, honoring ReconnectAutomatically,
    /// ReconnectImmediately and ReconnectIntervalSeconds from the current settings. A state change is always raised
    /// on the UI dispatcher, so this runs on the UI thread and no extra threading is involved.
    /// </summary>
    private void ScheduleAutoReconnect()
    {
        DeviceTerminalSettings settings = _deviceTerminalSettings;
        if (!CanAutoReconnect || settings == null || !settings.ReconnectAutomatically)
        {
            return;
        }
        StopAutoReconnectTimer();
        int delayMs = _reconnectPolicy.NextDelayMs(settings.ReconnectImmediately, settings.ReconnectIntervalSeconds * 1000);
        if (delayMs <= 0)
        {
            // Open the new connection right away, once the current state change has finished being handled.
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(new Action(ReconnectIfAutoStillWanted));
                }
                catch (InvalidOperationException)
                {
                    // The window is closing; drop the scheduled reconnect.
                }
            }
            return;
        }
        if (_autoReconnectTimer == null)
        {
            _autoReconnectTimer = new System.Windows.Forms.Timer();
            _autoReconnectTimer.Tick += OnAutoReconnectTimerTick;
        }
        _autoReconnectTimer.Interval = delayMs;
        _autoReconnectTimer.Start();
    }

    private void StopAutoReconnectTimer()
    {
        _autoReconnectTimer?.Stop();
    }

    private void OnAutoReconnectTimerTick(object sender, EventArgs e)
    {
        ((System.Windows.Forms.Timer)sender).Stop();
        ReconnectIfAutoStillWanted();
    }

    /// <summary>
    /// Reopens the connection that dropped, if the user has not explicitly disconnected in the meantime.
    /// Whether the screen content is wiped follows the ClearContentsWhenReconnecting setting.
    /// </summary>
    private void ReconnectIfAutoStillWanted()
    {
        if (CanAutoReconnect && _deviceTerminalSettings != null)
        {
            // After a connection that ended at once the screen keeps the last output (it usually says why the server
            // hung up) instead of being wiped on every round of the loop.
            bool resetTerminal = _deviceTerminalSettings.ClearContentsWhenReconnecting && _reconnectPolicy.FailedAttempts == 0;
            Connect(resetTerminal: resetTerminal, automatic: true);
        }
    }

    /// <summary>Pastes the clipboard text into the device (same as the context menu command).</summary>
    public void Paste()
    {
        pasteToolStripMenuItem_Click(this, EventArgs.Empty);
    }

    /// <summary>Copies the whole output (text or HEX dump, depending on the mode) to the clipboard.</summary>
    public void CopyAll()
    {
        copyAllToolStripMenuItem_Click(this, EventArgs.Empty);
    }

    /// <summary>Moves the keyboard focus to the active output view so typed characters reach the device.</summary>
    public void FocusTerminal()
    {
        if (HexMode)
        {
            hexTerminalControl1.Focus();
        }
        else
        {
            _serialTerminalControl.Focus();
        }
    }

    private void OnConnectionStateChanged(object sender, ConnectionStateEnum connectionStateEnum, string value)
    {
        _connectionState = connectionStateEnum;
        if (connectionStateEnum == ConnectionStateEnum.Connected)
        {
            _reconnectPolicy.OnConnected(TrafficClock.ElapsedMilliseconds);
        }
        else if (connectionStateEnum == ConnectionStateEnum.Disconnected || connectionStateEnum == ConnectionStateEnum.Failed)
        {
            if (_manualDisconnect)
            {
                _reconnectPolicy.Reset();
            }
            else
            {
                _reconnectPolicy.OnEnded(TrafficClock.ElapsedMilliseconds);
            }
        }
        try
        {
            ApplyConnectionState(connectionStateEnum, value);
            UpdateSerialLineUi();
            if (connectionStateEnum == ConnectionStateEnum.Connected)
            {
                _serialTerminalControl.BeginNewSession();
                DeliverPendingGzipBase64Recovery();
            }
        }
        finally
        {
            if (connectionStateEnum == ConnectionStateEnum.Connected || connectionStateEnum == ConnectionStateEnum.Connecting)
            {
                StopAutoReconnectTimer();
            }
            RaiseStatusChanged();
        }
        if (connectionStateEnum == ConnectionStateEnum.Disconnected || connectionStateEnum == ConnectionStateEnum.Failed)
        {
            ScheduleAutoReconnect();
        }
    }

    private void ApplyConnectionState(ConnectionStateEnum connectionStateEnum, string value)
    {
        // A connection that broke (a pulled cable, a reset) offers the same "try again" panel as a failed attempt; one that was
        // closed on purpose, by the user or by the server, keeps the last output visible with the toolbar's reconnect button.
        bool broke = connectionStateEnum == ConnectionStateEnum.Disconnected && !_manualDisconnect && !string.IsNullOrEmpty(value);
        if (connectionStateEnum == ConnectionStateEnum.Connecting || connectionStateEnum == ConnectionStateEnum.Failed || broke)
        {
            pnlConnectionInProgress.Visible = true;
            toolStrip1.Visible = false;
            progressBar1.Visible = connectionStateEnum == ConnectionStateEnum.Connecting;
            linkLabel1.Visible = connectionStateEnum != ConnectionStateEnum.Connecting;
        }
        else
        {
            pnlConnectionInProgress.Visible = false;
            toolStrip1.Visible = true;
            btnReconnect.Visible = connectionStateEnum != ConnectionStateEnum.Connected;
            btnDisconnect.Visible = connectionStateEnum == ConnectionStateEnum.Connected;
        }
        switch (connectionStateEnum)
        {
            case ConnectionStateEnum.Connecting:
                lblConnecting.Text = value;
                break;
            case ConnectionStateEnum.Disconnected:
                {
                    SerialTerminalControl terminal = _serialTerminalControl;
                    bool changed = (hexTerminalControl1.HideCaret = true);
                    terminal.HideCaret = changed;
                    Color backColor = (hexTerminalControl1.BackColor = _deviceTerminalSettings.Colors.Disconnected);
                    terminal.BackColor = backColor;
                    ToolStripLabel toolStripLabel = lblConnection;
                    // A reason is given when the connection broke (an unplugged adapter, a reset) instead of being closed.
                    string text = (lblConnecting.Text = string.IsNullOrEmpty(value) ? "Connection closed" : "Connection closed: " + value);
                    toolStripLabel.Text = text;
                    break;
                }
            case ConnectionStateEnum.Failed:
                lblConnecting.Text = "Connection failed: " + value;
                break;
            case ConnectionStateEnum.Connected:
                {
                    SerialTerminalControl terminal = _serialTerminalControl;
                    Color backColor = (hexTerminalControl1.BackColor = _deviceTerminalSettings.Colors.Background);
                    terminal.BackColor = backColor;
                    bool updated = (hexTerminalControl1.HideCaret = false);
                    terminal.HideCaret = updated;
                    break;
                }
        }
    }

    // A test of a network connection gives up after this long instead of the operating system's connect timeout.
    private const int TestConnectTimeoutMs = 10000;

    /// <summary>
    /// Opens the connection once and closes it again; throws with the reason when it cannot be made. It blocks, so the
    /// caller runs it on a worker thread.
    /// </summary>
    public static string TestConnection(DeviceTerminalSettings.ConnectionSettings connectionSettings)
    {
        using IDeviceTransport deviceTransport = new CallbackTransportFactory(CreateTransportCallback(connectionSettings) ?? throw new Exception("Connection type not specified")).CreateTransport();
        if (deviceTransport == null)
        {
            throw new Exception("Unspecified error");
        }
        switch (deviceTransport)
        {
            case TcpTransport tcpTransport:
                tcpTransport.ConnectTimeoutMs = TestConnectTimeoutMs;
                break;
            case TelnetTransport telnetTransport:
                telnetTransport.ConnectTimeoutMs = TestConnectTimeoutMs;
                break;
        }
        deviceTransport.Open(null);
        return deviceTransport.GetDisplayName(includeParameters: true);
    }

    private void OnDataReceived(object sender, byte[] buffer, int value)
    {
        Interlocked.Add(ref _bytesReceived, value);
        _consoleTap?.Invoke(buffer, value);
        // Only what the transfer's own connection sends belongs to the protocol; the output of a newer session (banner,
        // login prompt) is shown as usual.
        if (_isXModemActive && ReferenceEquals(sender, _lineOwner))
        {
            for (int i = 0; i < value; i++)
            {
                _xmodemRxQueue.Add(buffer[i]);
            }
            return;
        }

        // A ZMODEM sender announces itself with a recognizable header: take over the line from that point on, the
        // text before it is shown as usual.
        if (_autoStartZModem && !IsLineClaimed)
        {
            int zmodemStart = FindZModemRequest(buffer, value);
            if (zmodemStart >= 0)
            {
                if (zmodemStart > 0)
                {
                    DisplayReceivedData(buffer, zmodemStart);
                }
                if (BeginAutoZModemReceive(buffer, zmodemStart, value))
                {
                    return;
                }
                DisplayReceivedData(buffer, value - zmodemStart, zmodemStart);
                return;
            }
        }
        DisplayReceivedData(buffer, value);
    }

    /// <summary>Shows received bytes (<paramref name="count"/> of them, starting at <paramref name="offset"/>) in the active view and records them in the log.</summary>
    private void DisplayReceivedData(byte[] buffer, int count, int offset = 0)
    {
        // A slow paste looks for the echo of its line or the prompt in what the device sends.
        _pasteJob?.OnOutput(_textEncoding.GetString(buffer, offset, count));
        if (offset != 0)
        {
            byte[] slice = new byte[count];
            Buffer.BlockCopy(buffer, offset, slice, 0, count);
            buffer = slice;
        }
        // The log gets the raw stream before it is split into text or hex, so recording does not depend on the display mode.
        if (_logWriter != null)
        {
            _logWriter.Write(buffer, 0, count);
            int now = Environment.TickCount;
            if (now - _lastLogButtonUpdate > 500)
            {
                _lastLogButtonUpdate = now;
                UpdateLogButton();
            }
        }
        if (HexMode)
        {
            hexTerminalControl1.AppendData(buffer, count);
            return;
        }
        string text = _textDecoder.Decode(buffer, 0, count);
        if (_deviceTerminalSettings != null && _deviceTerminalSettings.TreatLFAsCRLF)
        {
            text = text.Replace("\n", "\r\n");
        }
        _serialTerminalControl.OnDataReceived(text);
    }

    /// <summary>
    /// Runs one XMODEM transfer on a worker thread with a progress window. Must be called on the UI thread: the line is
    /// claimed here, before the first byte can arrive, and a second transfer cannot be started meanwhile.
    /// </summary>
    private void StartXModemTransfer(string title, string verb, Func<FileStream> fileStream, bool isSend, bool useLargeBlocks = false)
    {
        string protocolName = useLargeBlocks ? "XMODEM-1K" : "XMODEM";
        StartLineTransfer(title, protocolName, verb, (sendData, progress, token) =>
        {
            using FileStream stream = fileStream();
            return isSend
                ? SmarTTY.IO.XModemTransfer.SendFile(stream, sendData, ReadXModemByte, token, progress, useLargeBlocks)
                : SmarTTY.IO.XModemTransfer.ReceiveFile(stream, sendData, ReadXModemByte, token, progress);
        });
    }

    /// <summary>
    /// How much a transfer may have queued for the device and not yet written. A protocol that waits for answers never
    /// gets near it. YMODEM-G streams without waiting: without a limit the whole file would go into the queue at once,
    /// the progress window would race ahead of the line and a cancel (or the receiver's CAN CAN) would take effect
    /// only after the queue has drained.
    /// </summary>
    private const int MaxQueuedTransferBytes = 4096;

    /// <summary>The protocol part of a transfer. Runs on a worker thread and returns whether the transfer succeeded.</summary>
    private delegate bool LineTransferRun(
        SmarTTY.IO.XModemTransfer.SendDataHandler sendData,
        SmarTTY.IO.XModemTransfer.ProgressHandler progress,
        CancellationToken token);

    /// <summary>
    /// Runs one file transfer protocol (XMODEM, YMODEM, Kermit) on a worker thread with a progress window;
    /// <paramref name="run"/> is the protocol itself and gets the line (callbacks for sending bytes and reporting
    /// progress, the cancel token). <paramref name="successNote"/> (optional) is called on the UI thread after a
    /// successful transfer and may return an extra paragraph for the final message. <paramref name="afterEnd"/> (optional)
    /// is called on the UI thread when the transfer has ended, whatever its result, after the window has been closed.
    /// Must be called on the UI thread.
    /// </summary>
    private void StartLineTransfer(string title, string protocolName, string verb, LineTransferRun run, Func<string> successNote = null, Action afterEnd = null)
    {
        if (IsLineClaimed)
        {
            return;
        }

        // Not a "using": the window is modeless and outlives this method; it is closed (and disposed) when the transfer ends.
        ITransferProgressWindow transferForm = CreateTransferProgressWindow(title, showErrorCount: true);

        transferForm.Show();

        // Claim the line only after the window is up (a failing Show must not leave the terminal locked). Stale bytes
        // (a prompt, echo) must not be mistaken for the peer's first answer.
        while (_xmodemRxQueue.TryTake(out _)) { }
        AsyncTransportConnection owner = _asyncTransportConnection;
        _lineOwner = owner;
        _cancelLineTransfer = transferForm.Cancel;
        _isXModemActive = true;

        // The callback parameter must not be called "_": a lone "_" parameter is an ordinary variable, so the
        // "out _" discards would bind to it (an object) instead of discarding, and fail to compile (CS1503).
        ThreadPool.QueueUserWorkItem(state =>
        {
            bool success = false;
            string error = null;
            try
            {
                object queueGate = new object();
                int queuedBytes = 0; // handed to the transport and not yet written
                SmarTTY.IO.XModemTransfer.SendDataHandler sendData = (data, offset, length) =>
                {
                    // Writes are queued, so a failure of an earlier one is only known now: end the transfer with its reason
                    // instead of letting it wait for answers that cannot come.
                    string writeError = owner?.WriteError;
                    if (writeError != null)
                    {
                        throw new IOException(writeError);
                    }
                    lock (queueGate)
                    {
                        // Wait for the line to catch up; a cancel or an ended connection ends the wait (the caller notices them).
                        while (queuedBytes > MaxQueuedTransferBytes && !transferForm.IsCancelled && owner?.HasEnded == false)
                        {
                            Monitor.Wait(queueGate, 100);
                        }
                        queuedBytes += length;
                    }

                    bool queued = false;
                    try
                    {
                        byte[] copy = new byte[length];
                        Array.Copy(data, offset, copy, 0, length);
                        queued = SendToConnection(owner, copy, length, written =>
                        {
                            lock (queueGate)
                            {
                                queuedBytes -= length;
                                Monitor.PulseAll(queueGate);
                            }
                        });
                    }
                    finally
                    {
                        if (!queued)
                        {
                            // Nothing will report this write as done.
                            lock (queueGate)
                            {
                                queuedBytes -= length;
                                Monitor.PulseAll(queueGate);
                            }
                        }
                    }
                    if (!queued)
                    {
                        throw new IOException("The connection is closed.");
                    }
                };
                SmarTTY.IO.XModemTransfer.ProgressHandler progress = (bytesTransferred, totalBytes, errorCount, status) =>
                    transferForm.UpdateProgress(bytesTransferred, totalBytes, errorCount, status);
                success = run(sendData, progress, transferForm.CancellationToken);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                transferForm.UpdateProgress(0, 0, 1, "Error: " + ex.Message);
            }
            finally
            {
                _isXModemActive = false;
                _lineOwner = null;
                _cancelLineTransfer = null;
                while (_xmodemRxQueue.TryTake(out _)) { } // Clear leftovers
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    bool cancelled = transferForm.IsCancelled;
                    string lastStatus = transferForm.LastStatus;
                    transferForm.Dispose();
                    if (afterEnd != null)
                    {
                        try
                        {
                            afterEnd();
                        }
                        catch (InvalidOperationException)
                        {
                            // The connection is gone: nothing to tell the device.
                        }
                    }
                    if (success)
                    {
                        string note = successNote?.Invoke();
                        ThemedMessageBox.Show(protocolName + " " + verb + " completed successfully." + (string.IsNullOrEmpty(note) ? string.Empty : "\r\n\r\n" + note),
                            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (!cancelled)
                    {
                        string reason = error ?? lastStatus;
                        ThemedMessageBox.Show(protocolName + " " + verb + " failed." + (string.IsNullOrEmpty(reason) ? string.Empty : "\r\n\r\n" + reason), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // The terminal was closed while the transfer was running: nobody is left to show the result to.
            }
        });
    }

    private byte? ReadXModemByte(int timeoutMs)
    {
        if (_xmodemRxQueue.TryTake(out byte b, timeoutMs))
        {
            return b;
        }
        return null;
    }

    /// <summary>True while an XMODEM or ZMODEM transfer or a file being sent owns the line: nothing else may get into the data stream.</summary>
    private bool IsLineClaimed => _isXModemActive || _isFileSendActive;

    /// <summary>True (with a short explanation) while a transfer owns the line.</summary>
    private bool IsLineBusy()
    {
        if (_isXModemActive)
        {
            ThemedMessageBox.Show("A file transfer (XMODEM, YMODEM, ZMODEM, Kermit or gzip + base64) is in progress. Wait for it to finish or cancel it first.", "File transfer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        if (_isFileSendActive)
        {
            ThemedMessageBox.Show("A file is being sent. Wait for it to finish or cancel it first.", "Send file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        return false;
    }

    private void sendFileViaXMODEMToolStripMenuItem_Click(object sender, EventArgs e)
    {
        SendFileViaXModem(useLargeBlocks: false);
    }

    private void sendFileViaXMODEM1KToolStripMenuItem_Click(object sender, EventArgs e)
    {
        SendFileViaXModem(useLargeBlocks: true);
    }

    private void SendFileViaXModem(bool useLargeBlocks)
    {
        string protocolName = useLargeBlocks ? "XMODEM-1K" : "XMODEM";

        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before sending a file via " + protocolName + ".", protocolName + " Send", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string fileName = ThemedFileDialog.OpenFile(this, "Select file to send via " + protocolName, "All files (*.*)|*.*");
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        // The stream is opened later, on the worker thread: only the name is needed here.
        StartXModemTransfer(protocolName + " Send", "send", fileStream: () => File.OpenRead(fileName), isSend: true, useLargeBlocks: useLargeBlocks);
    }

    private void receiveFileViaXMODEMToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before receiving a file via XMODEM.", "XMODEM Receive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string fileName = ThemedFileDialog.SaveFile(this, "Select destination file for XMODEM receive", "All files (*.*)|*.*");
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        // See the send handler: only the name is needed here; the worker thread opens the file.
        StartXModemTransfer("XMODEM Receive", "receive", fileStream: () => File.Create(fileName), isSend: false);
    }

    /// <summary>
    /// Feeds previously recorded received data (see the log replay window) back into this terminal exactly like
    /// live data: hex bytes into the hex dump in HEX mode, decoded text into the text terminal otherwise. Nothing is
    /// recorded and nothing is sent to the device.
    /// </summary>
    public void ReplayReceivedData(byte[] buffer, int count)
    {
        if (buffer == null || count <= 0 || _logWriter != null)
        {
            return;
        }
        count = Math.Min(count, buffer.Length);
        if (HexMode)
        {
            // AppendData appends from index 0, so the caller passes a fresh slice for every step.
            hexTerminalControl1.AppendData(buffer, count);
            return;
        }
        string text = _textDecoder.Decode(buffer, 0, count);
        if (_deviceTerminalSettings != null && _deviceTerminalSettings.TreatLFAsCRLF)
        {
            text = text.Replace("\n", "\r\n");
        }
        // Queries recorded in the log (cursor position, device attributes) must not be answered to the device.
        _serialTerminalControl.SuppressReplies = true;
        try
        {
            _serialTerminalControl.OnDataReceived(text);
        }
        finally
        {
            _serialTerminalControl.SuppressReplies = false;
        }
    }

    /// <summary>Resets the incremental decoder so a replay can safely restart from the beginning of a log.</summary>
    public void ResetReplayDecoder()
    {
        _textDecoder.Reset();
    }

    private void OnTerminalDataEntered(object sender, byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
        if (IsLineClaimed)
        {
            return; // typed characters would corrupt the transfer
        }
        if (_asyncTransportConnection != null)
        {
            SendToDevice(buffer, buffer.Length);
        }
        // Echo is now handled by SerialTerminalControl locally
        // The device may also echo back, which will be handled by OnDataReceived
    }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        Connect();
    }

    private void btnDisconnect_Click(object sender, EventArgs e)
    {
        Disconnect();
    }

    private void btnReconnect_Click(object sender, EventArgs e)
    {
        Connect();
    }

    private void btnSend_ButtonClick(object sender, EventArgs e)
    {
        if (!btnSend.Enabled || IsLineBusy())
        {
            return;
        }
        try
        {
            byte[] items = ((!sendAsHEXToolStripMenuItem.Checked) ? _textEncoding.GetBytes(txtHexInput.Text) : DeviceInputText.ParseHex(txtHexInput.Text));
            SendToDevice(items, items.Length);
            if (_deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(items, items.Length, isEcho: true);
            }
            if ((Control.ModifierKeys & Keys.Alt) == 0)
            {
                txtHexInput.Text = "";
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void sendAsHEXToolStripMenuItem_Click(object sender, EventArgs e)
    {
        sendAsHEXToolStripMenuItem.Checked = !sendAsHEXToolStripMenuItem.Checked;
    }

    private void txtHexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            btnSend_ButtonClick(sender, e);
            e.SuppressKeyPress = true;
        }
    }

    private void txtHexInput_TextChanged(object sender, EventArgs e)
    {
        if (!sendImmediatelyToolStripMenuItem.Checked)
        {
            return;
        }
        if (sendAsHEXToolStripMenuItem.Checked)
        {
            // Sent once a byte is complete: two digits ("41", "0x41"), or one digit closed by a separator ("A ").
            if (DeviceInputText.IsHexReadyToSend(txtHexInput.Text))
            {
                btnSend_ButtonClick(null, null);
            }
        }
        else if (txtHexInput.Text.Length > 0)
        {
            btnSend_ButtonClick(null, null);
        }
    }

    private void hexTerminalControl1_KeyPress(object sender, KeyPressEventArgs e)
    {
        // In character mode any printable character is accepted (Cyrillic included); it is encoded with the selected encoding on send.
        if (!char.IsControl(e.KeyChar) && (!sendAsHEXToolStripMenuItem.Checked || DeviceInputText.IsHexInputCharacter(e.KeyChar)))
        {
            txtHexInput.Text += e.KeyChar;
            txtHexInput.SelectionStart = txtHexInput.Text.Length;
            txtHexInput.SelectionLength = 0;
        }
        txtHexInput.Focus();
    }

    private void sendImmediatelyToolStripMenuItem_Click(object sender, EventArgs e)
    {
        sendImmediatelyToolStripMenuItem.Checked = !sendImmediatelyToolStripMenuItem.Checked;
    }

    private void txtHexInput_KeyPress(object sender, KeyPressEventArgs e)
    {
        // Control characters stay allowed: Backspace, and Ctrl+V / Ctrl+C / Ctrl+A that the text box handles itself.
        if (sendAsHEXToolStripMenuItem.Checked && !char.IsControl(e.KeyChar) && !DeviceInputText.IsHexInputCharacter(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    private void toolStripButton1_Click(object sender, EventArgs e)
    {
        ClearOutput();
    }

    public void ClearOutput()
    {
        // Keeps a command that is being typed (and its prompt) at the top of the cleared screen.
        _serialTerminalControl.ClearScreen();
        hexTerminalControl1.Clear();
    }

    private void toolStripButton2_Click(object sender, EventArgs e)
    {
        string fileName = ThemedFileDialog.SaveFile(this, "Save output to a file", "All files (*.*)|*.*");
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }
        try
        {
            if (HexMode)
            {
                File.WriteAllBytes(fileName, hexTerminalControl1.AllBytes);
            }
            else
            {
                File.WriteAllText(fileName, _serialTerminalControl.Text);
            }
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void toolStripButton3_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before sending a file.", "Send file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string fileName = ThemedFileDialog.OpenFile(this, "Send a binary file", "All files (*.*)|*.*");
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }
        StartFileSend(fileName);
    }

    private void copyAllToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (HexMode)
        {
            Win32Clipboard.SetText(Handle, hexTerminalControl1.HexText);
        }
        else
        {
            Win32Clipboard.SetText(Handle, _serialTerminalControl.Text);
        }
    }

    private void copyToolStripMenuItem_Click(object sender, EventArgs e)
    {
        CopySelection();
    }

    public void CopySelection()
    {
        if (HexMode)
        {
            hexTerminalControl1.CopySelection();
        }
        else if (((ReadonlyTextBrowser)_serialTerminalControl).HasSelection)
        {
            _serialTerminalControl.CopySelection(select: false);
        }
    }

    private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e)
    {
        copyToolStripMenuItem.Enabled = HexMode ? hexTerminalControl1.HasSelection : ((ReadonlyTextBrowser)_serialTerminalControl).HasSelection;
        _returnToInputLineMenuItem.Enabled = !HexMode && !_serialTerminalControl.HasInputLine;
        _sendRemoteSizeMenuItem.Enabled = !HexMode && _serialTerminalControl.CanSendRemoteSize;
    }

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        GlobalSettings common = _commonSettings;
        if (common != null && common.UseCtrlShortcuts && e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.W)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CloseTabRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // Ctrl+D closes the terminal window (same as Ctrl+W). It is not gated by the "Ctrl shortcuts"
        // option so the classic serial-terminal shortcut works out of the box, and the key is not
        // forwarded to the device.
        if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.D)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CloseTabRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // The menu with the history and the snippets: Ctrl+Shift+Insert, and Ctrl+Shift+V when the settings say so.
        if (e.Control && e.Shift && !e.Alt
            && (e.KeyCode == Keys.Insert || (e.KeyCode == Keys.V && PasteGuard.OptionsOf(common).CtrlShiftV == PasteSettings.ShortcutAction.HistoryMenu)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShowPasteMenu();
            return;
        }
        if (!e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.Return)
        {
            // Not a character for the device: toggle full screen like in SSH tabs.
            e.Handled = true;
            e.SuppressKeyPress = true;
            FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // By default Ctrl+C is sent to the device (e.g. SIGINT for a Linux console), so clipboard
        // operations use Ctrl+Shift+C / Ctrl+Shift+V like in common Linux terminals. When the shared option
        // "Ctrl-C/Ctrl-X/Ctrl-V = Copy/Cut/Paste" is on, plain Ctrl+C / Ctrl+V work on the clipboard (as in SSH tabs).
        // The HEX view never sends Ctrl+C to the device, so plain Ctrl+C copies there as well.
        bool interceptClipboard = common != null && common.InterceptCtrlClipboardShortcuts;
        bool copy = e.Control && !e.Alt && e.KeyCode == Keys.C && (e.Shift || HexMode || interceptClipboard);
        bool paste = e.Control && !e.Alt && e.KeyCode == Keys.V && (e.Shift || interceptClipboard);
        if (copy)
        {
            CopySelection();
        }
        else if (paste)
        {
            pasteToolStripMenuItem_Click(sender, e);
        }
        else
        {
            return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private enum PasteKind
    {
        /// <summary>Text, except in the HEX view, where text that reads as bytes is sent as bytes.</summary>
        Auto,
        Text,
        HexBytes
    }

    private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
    {
        PasteToDevice(null, PasteKind.Auto);
    }

    private ToolStripMenuItem _pasteAsHexMenuItem;

    private ToolStripMenuItem _pasteAsTextMenuItem;

    private void AddPasteCommands()
    {
        ToolStripMenuItem history = new ToolStripMenuItem("Paste from history and snippets...", null, (sender, args) => ShowPasteMenu())
        {
            ShortcutKeyDisplayString = "Ctrl+Shift+Insert"
        };
        _pasteAsHexMenuItem = new ToolStripMenuItem("Paste as HEX bytes", null, (sender, args) => PasteToDevice(null, PasteKind.HexBytes))
        {
            ToolTipText = "The clipboard text such as DE AD BE EF is sent as bytes"
        };
        _pasteAsTextMenuItem = new ToolStripMenuItem("Paste as text", null, (sender, args) => PasteToDevice(null, PasteKind.Text))
        {
            ToolTipText = "Send the clipboard text as characters, even in the HEX view"
        };
        int index = contextMenuStrip1.Items.IndexOf(pasteToolStripMenuItem);
        index = index < 0 ? contextMenuStrip1.Items.Count : index + 1;
        contextMenuStrip1.Items.Insert(index, history);
        contextMenuStrip1.Items.Insert(index + 1, _pasteAsHexMenuItem);
        contextMenuStrip1.Items.Insert(index + 2, _pasteAsTextMenuItem);
    }

    /// <summary>The menu with the clipboard, the texts pasted lately and the snippets; the choice goes to the device.</summary>
    private void ShowPasteMenu()
    {
        Control owner = HexMode ? hexTerminalControl1 : (Control)_serialTerminalControl;
        PasteMenu.Show(owner, _commonSettings, null, () => PasteToDevice(null, PasteKind.Auto), text => PasteToDevice(text, PasteKind.Auto));
    }

    private void PasteToDevice(string clipboardText, PasteKind kind)
    {
        if (IsLineClaimed || (_pasteJob != null && _pasteJob.IsActive))
        {
            return;
        }
        try
        {
            string text = clipboardText ?? Win32Clipboard.GetText(Handle);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            PasteSettings options = PasteGuard.OptionsOf(_commonSettings);
            bool asHex = kind == PasteKind.HexBytes
                || (kind == PasteKind.Auto && HexMode && options.HexViewPastesBytes && DeviceInputText.TryParsePastedHex(text, out _));
            if (asHex)
            {
                if (!DeviceInputText.TryParsePastedHex(text, out byte[] hexBytes))
                {
                    ThemedMessageBox.Show("The clipboard does not hold a string of HEX bytes such as DE AD BE EF, 0xDE,0xAD or \\xDE\\xAD.", "Paste as HEX bytes", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                PasteHistory.Add(text, options.EffectiveHistorySize);
                SendToDevice(hexBytes, hexBytes.Length);
                if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
                {
                    hexTerminalControl1.AppendData(hexBytes, hexBytes.Length, isEcho: true);
                }
                return;
            }
            // A command wrapped by its source must not go out in pieces; every line break sends the line at once.
            text = PasteGuard.ApplyMode(text, _pasteMode);
            text = ShellCommandLine.JoinWrappedLines(text, indentContinues: false);
            text = PasteGuard.Prepare(text, _commonSettings, linesRunAtOnce: true, owner: this);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            // Each pasted line ends the way Enter ends a typed one, whatever the clipboard used (Windows CR LF, Unix LF),
            // unless the paste settings name a line break. Without a line ending in the connection settings the text keeps
            // the Windows form.
            string lineEnding = PasteGuard.ResolveLineBreak(_commonSettings, GetLineEnding(_deviceTerminalSettings.LineEnding) ?? "\r\n");
            text = DeviceInputText.ConvertLineEndings(text, lineEnding);
            // A single-byte code page lacks many characters: curly quotes get their ASCII form, anything else that is
            // missing is reported instead of reaching the device as "?".
            text = PasteGuard.FitToEncoding(text, _textEncoding, _commonSettings, this);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            bool slow = _pasteMode == PasteMode.Slow || options.SlowPasteAlways;
            if (!slow && text.Length < AsyncPasteThreshold)
            {
                byte[] bytes = _textEncoding.GetBytes(text);
                SendToDevice(bytes, bytes.Length);
                if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
                {
                    hexTerminalControl1.AppendData(bytes, bytes.Length, isEcho: true);
                }
                return;
            }
            // Sent in pieces, the next one when the previous has been written (and, for a slow paste, after the pauses and
            // the echo or the prompt of the device), with a progress window and Esc to cancel.
            _pasteJob = new PasteJob(this, slow ? "Pasting slowly" : "Pasting",
                PasteModes.BuildSteps(text, slow ? options.SlowChunkSize : AsyncPasteChunkSize, lineEnding, splitLines: slow),
                (string piece, Action written) =>
                {
                    byte[] bytes = _textEncoding.GetBytes(piece);
                    if (!SendToDevice(bytes, bytes.Length, (bool ok) => written()))
                    {
                        return false;
                    }
                    if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
                    {
                        hexTerminalControl1.AppendData(bytes, bytes.Length, isEcho: true);
                    }
                    return true;
                },
                options, slow,
                (PasteJob.Outcome outcome, string message) =>
                {
                    _pasteJob = null;
                    if (outcome == PasteJob.Outcome.Failed)
                    {
                        ThemedMessageBox.Show(message, "Paste", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                });
            _pasteJob.Start();
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void btnReplay_Click(object sender, EventArgs e)
    {
        if (_logWriter != null)
        {
            ThemedMessageBox.Show("Stop recording first, then replay.", "Replay log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string chosen = ThemedFileDialog.OpenFile(this, "Play back a recorded log of received data",
            "All files (*.*)|*.*|Text log (*.log)|*.log|Hex dump (*.txt)|*.txt|Raw bytes (*.bin)|*.bin");
        if (string.IsNullOrEmpty(chosen))
        {
            return;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(chosen);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (bytes.Length == 0)
        {
            ThemedMessageBox.Show("The file is empty.", "Replay log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        LogReplayPrompt?.Invoke(bytes, this);
    }

    private void btnLog_Click(object sender, EventArgs e)
    {
        if (_logWriter != null)
        {
            StopLogging();
        }
        else
        {
            StartLogging();
        }
    }

    private void StartLogging()
    {
        // The format follows the chosen file type; hex mode suggests the hex dump, text mode the text log.
        ThemedFileDialogResult chosen = ThemedFileDialog.SaveFileEx(this, "Record received data (appended if the file already exists)",
            "Text log (UTF-8) (*.log)|*.log|Hex dump (*.txt)|*.txt|Raw bytes (*.bin)|*.bin",
            filterIndex: HexMode ? 2 : 1, fileName: SuggestLogFileName(), overwritePrompt: false, addExtension: true);
        if (chosen == null)
        {
            return;
        }
        TerminalLogFormat format = chosen.FilterIndex switch
        {
            2 => TerminalLogFormat.HexDump,
            3 => TerminalLogFormat.RawBytes,
            _ => TerminalLogFormat.Text,
        };
        try
        {
            TerminalLogWriter writer = new TerminalLogWriter(chosen.FileName, format, _textEncoding, append: true);
            writer.Failed += OnLogFailed;
            _logWriter = writer;
        }
        catch (Exception exception)
        {
            ThemedMessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        UpdateLogButton();
    }

    private void StopLogging()
    {
        TerminalLogWriter writer = _logWriter;
        _logWriter = null;
        if (writer != null)
        {
            writer.Failed -= OnLogFailed;
            writer.Dispose();
        }
        btnLog.Text = "Start log";
        btnLog.ForeColor = _toolbarPalette.Text;
        btnLog.ToolTipText = "Continuously record received data to a file";
    }

    private void UpdateLogButton()
    {
        TerminalLogWriter writer = _logWriter;
        if (writer == null)
        {
            return;
        }
        string format = writer.Format switch
        {
            TerminalLogFormat.HexDump => "a hex dump",
            TerminalLogFormat.RawBytes => "raw bytes",
            _ => "text (UTF-8)",
        };
        btnLog.Text = "Stop log (" + FormatByteCount(writer.BytesWritten) + ")";
        btnLog.ForeColor = _toolbarPalette.Error;
        btnLog.ToolTipText = "Recording to " + writer.FilePath + " as " + format + ". Click to stop.";
    }

    private void OnLogFailed(Exception exception)
    {
        // Raised from inside the data callback: stop and report after it has finished, and only if
        // the failed writer is still the current one (the user may have restarted logging meanwhile).
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (_logWriter != null && !_logWriter.IsActive)
                {
                    StopLogging();
                    ThemedMessageBox.Show("Logging stopped: " + exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private string SuggestLogFileName()
    {
        string name = _deviceTerminalSettings?.Connection?.FileNameBase;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "terminal";
        }
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.0") + " KB";
        }
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    }

    private void cbASCII_Click(object sender, EventArgs e)
    {
        DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.ASCII;
    }

    private void cbHEX_Click(object sender, EventArgs e)
    {
        DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.HEX;
    }

    private void cbBoth_Click(object sender, EventArgs e)
    {
        DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.Both;
    }

    private void echoTypedCharactersToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet)
        {
            // For telnet the item switches the echo of this session until the server's echo changes (or a reconnect).
            SetTelnetLocalEcho(!_telnetLocalEcho);
            return;
        }
        DeviceTerminalSettings deviceTerminalSettings = _deviceTerminalSettings;
        bool echoTypedCharacters = (echoTypedCharactersToolStripMenuItem.Checked = !echoTypedCharactersToolStripMenuItem.Checked);
        deviceTerminalSettings.EchoTypedCharacters = echoTypedCharacters;
        _serialTerminalControl.LocalEcho = echoTypedCharacters;
    }

    public void Disconnect(string text = null)
    {
        AsyncTransportConnection asyncTransportConnection = _asyncTransportConnection;
        if (asyncTransportConnection != null && asyncTransportConnection.MatchesPort(text))
        {
            // A deliberately closed connection must not be reopened automatically: this is the cancellation point
            // that the auto-reconnect logic (which is also fed by OnConnectionStateChanged) must honor.
            _manualDisconnect = true;
            StopAutoReconnectTimer();
            _asyncTransportConnection.Dispose();
            _asyncTransportConnection = null;
            OnConnectionStateChanged(null, ConnectionStateEnum.Disconnected, null);
        }
    }

    private void changeSettingsToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (!(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial))
        {
            return;
        }
        if (SerialSettingsPrompt == null)
        {
            return;
        }
        (bool accepted, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings chosen) =
            SerialSettingsPrompt(serial.AdvancedSettings, serial.ComPortName);
        if (accepted)
        {
            TryApplySerialSettings(serial, chosen);
            cbBaudRate.Text = (serial.AdvancedSettings?.BaudRate)?.ToString() ?? cbBaudRate.Text;
        }
    }

    private void cbBaudRate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            ApplyBaudRate(cbBaudRate.Text);
        }
    }

    private void cbBaudRate_SelectedIndexChanged(object sender, EventArgs e)
    {
        ApplyBaudRate(cbBaudRate.Text);
    }

    private void ApplyBaudRate(string text)
    {
        if (!(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial))
        {
            return;
        }
        int currentBaudRate = serial.AdvancedSettings?.BaudRate ?? 0;
        if (currentBaudRate == 0 && string.IsNullOrWhiteSpace(text))
        {
            // The port uses the driver's settings and nothing was typed: leaving the box is not a request to change them.
            return;
        }
        if (!SerialPortRules.TryParseBaudRate(text, out int baudRate, out string error))
        {
            // Not a dialog: this runs from Leave and Enter, where a message box would steal the focus again.
            ShowPortProblem("Baud rate: " + error);
            if (currentBaudRate > 0)
            {
                cbBaudRate.Text = currentBaudRate.ToString();
            }
            return;
        }
        if (baudRate == currentBaudRate)
        {
            return;
        }
        DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings candidate =
            serial.AdvancedSettings?.Clone() ?? new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings();
        candidate.BaudRate = baudRate;
        if (!TryApplySerialSettings(serial, candidate) && currentBaudRate > 0)
        {
            cbBaudRate.Text = currentBaudRate.ToString();
        }
    }

    private void cbBaudRate_Leave(object sender, EventArgs e)
    {
        ApplyBaudRate(cbBaudRate.Text);
    }
}
