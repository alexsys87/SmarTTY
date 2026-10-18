using SmarTTY.Forms;
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

    private Brush _brush;

    private bool _isHexMode;

    // Text exchanged with the device (received text, typed and pasted input, echo, "Send" box) always goes
    // through this one encoding, so what is shown and what is sent cannot disagree with each other.
    private Encoding _textEncoding = Encoding.UTF8;

    // Chunks of the stream are decoded with state, so a multi-byte character split between two reads is not corrupted.
    private TextStreamDecoder _textDecoder = new TextStreamDecoder(Encoding.UTF8);

    private bool _updatingEncodingSelection;

    // Continuous log of received data; null while not recording.
    private TerminalLogWriter _logWriter;

    private int _lastLogButtonUpdate;

    private long _bytesSent;

    private long _bytesReceived;

    private ConnectionStateEnum _connectionState = ConnectionStateEnum.Connecting;

    // Automatic reconnect: when the transport drops or fails and DeviceTerminalSettings.ReconnectAutomatically is
    // set, a fresh connection is opened again, right away or on a timer (ReconnectIntervalSeconds). Only the user's
    // own disconnect cancels it (_manualDisconnect); everything runs on the UI thread (WinForms timer).
    private bool _manualDisconnect;

    private System.Windows.Forms.Timer _autoReconnectTimer;

    /// <summary>Raised on the UI thread whenever the transport state or the displayed title changes.</summary>
    public event EventHandler StatusChanged;

    /// <summary>
    /// The text terminal while it has the keyboard focus (null in HEX mode or when the focus is in the toolbar), so
    /// window-level key handling (F10, Alt+letter) never leaks into the baud rate box or the "Send" field.
    /// </summary>
    internal SerialTerminalControl FocusedTextTerminal => (!_isHexMode && _serialTerminalControl.Focused) ? _serialTerminalControl : null;

    /// <summary>
    /// The editor currently shown in the device tab: the decoded text in ASCII mode, or the HEX dump editor in
    /// HEX mode. The shared search bar searches it to find both words and HEX byte sequences.
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

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

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
            DeviceTerminalSettings.ConnectionSettings.Tcp tcp => tcp.Host + ":" + tcp.Port,
            _ => "Raw Terminal",
        };
    }

    /// <summary>One-line description for the status bar, e.g. "COM3 (115200 bps): connected".</summary>
    public string StatusText
    {
        get
        {
            string name = _transportDisplayName ?? ConnectionTitle;
            return _connectionState switch
            {
                ConnectionStateEnum.Connecting => name + ": connecting...",
                ConnectionStateEnum.Connected => name + ": connected",
                ConnectionStateEnum.Failed => name + ": connection failed",
                _ => name + ": disconnected",
            };
        }
    }

    private string _transportDisplayName;

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HexMode
    {
        get
        {
            return _isHexMode;
        }
        set
        {
            _isHexMode = value;
            hexTerminalControl1.Visible = value;
            _serialTerminalControl.Visible = !value;
            cbASCII.Checked = !value;
            cbHEX.Checked = value;
            ToolStripLabel toolStripLabel = lblSend;
            ToolStripTextBox toolStripTextBox = txtHexInput;
            bool isVisible = (btnSend.Visible = value);
            bool visible = (toolStripTextBox.Visible = isVisible);
            toolStripLabel.Visible = visible;
        }
    }

    private SerialTerminalControl _serialTerminalControl;

    public DeviceTerminalControl()
    {
        InitializeComponent();
        DpiAwareForm.ConfigureToolStrip(toolStrip1);
        Disposed += DeviceTerminalControl_Disposed;
        pnlConnectionInProgress.BringToFront();
        toolStrip1.SendToBack();
        
        // Replace the designer-created TerminalEmulatorControl with our SerialTerminalControl
        ReplaceTerminalControl();
        // A telnet server may ask for the terminal size (and wants to hear about changes); other transports ignore it.
        _serialTerminalControl.TerminalSizeChanged += OnTerminalSizeChanged;
        
        foreach (TerminalEncodings.Choice choice in TerminalEncodings.All)
        {
            cbEncoding.Items.Add(choice);
        }
        SelectEncodingInToolbar(TerminalEncodings.DefaultName);
    }

    private void OnTerminalSizeChanged(TerminalEmulatorControl terminal, int columns, int rows)
    {
        _asyncTransportConnection?.SetTerminalSize(columns, rows);
    }

    // --- telnet: local echo that follows the server, and the "Telnet" commands in the context menu ---

    private TelnetTransport _telnetTransport;

    private ToolStripMenuItem _telnetMenu;

    private void SetLocalEcho(bool echo)
    {
        // The same three things the "Echo typed characters" menu item changes.
        _deviceTerminalSettings.EchoTypedCharacters = echo;
        echoTypedCharactersToolStripMenuItem.Checked = echo;
        _serialTerminalControl.LocalEcho = echo;
    }

    // Called for every new telnet connection (also when it is reconnected): applies the local echo mode and, in
    // the automatic mode, keeps the echo opposite to the server's.
    private void AttachTelnet(TelnetTransport transport, DeviceTerminalSettings.ConnectionSettings.Telnet settings)
    {
        _telnetTransport = transport;
        switch (settings.LocalEcho)
        {
            case DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Always:
                SetLocalEcho(true);
                break;
            case DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Never:
                SetLocalEcho(false);
                break;
            default:
                // Nobody echoes until the server says it does.
                SetLocalEcho(true);
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
                    SetLocalEcho(!transport.RemoteEcho);
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
            AddTelnetCommand("Send Break", TelnetStream.Break);
            AddTelnetCommand("Send Interrupt Process (IP)", TelnetStream.InterruptProcess);
            AddTelnetCommand("Send Are You There (AYT)", TelnetStream.AreYouThere);
            AddTelnetCommand("Send Erase Line", TelnetStream.EraseLine);
            AddTelnetCommand("Send NOP", TelnetStream.NOP);
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

    private void AddTelnetCommand(string text, byte command)
    {
        _telnetMenu.DropDownItems.Add(new ToolStripMenuItem(text, null, (sender, args) =>
        {
            try
            {
                _telnetTransport?.SendCommand(command);
            }
            catch (Exception)
            {
                // The connection is gone; the status line says so.
            }
        }));
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

        // Add to controls at the same position as the old control
        Controls.Add(_serialTerminalControl);
        Controls.SetChildIndex(_serialTerminalControl, Controls.GetChildIndex(hexTerminalControl1));
    }

    private void SetTextEncoding(string encodingName)
    {
        _textEncoding = TerminalEncodings.Resolve(encodingName);
        _textDecoder = new TextStreamDecoder(_textEncoding);
        _serialTerminalControl.InputEncoding = _textEncoding;
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

    internal static CallbackTransportFactory.TransportCallback CreateTransportCallback(DeviceTerminalSettings.ConnectionSettings connectionSettings)
    {
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Serial)
        {
            return () => new SerialTransport(connectionSettings as DeviceTerminalSettings.ConnectionSettings.Serial);
        }
        // Telnet derives from Tcp, so it has to be recognized first.
        if (connectionSettings is DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
        {
            return () => new TelnetTransport(telnet);
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
            if (!string.IsNullOrEmpty(settings.FontName) && settings.FontSize != 0f)
            {
                hexTerminalControl1.Font = new Font(settings.FontName, settings.FontSize);
                terminal.Font = new Font(settings.FontName, settings.FontSize);
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
        CallbackTransportFactory.TransportCallback callback = CreateTransportCallback(deviceTerminalSettings.Connection);
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
        SetTextEncoding(deviceTerminalSettings.TextEncoding);
        SelectEncodingInToolbar(deviceTerminalSettings.TextEncoding);
        _brush = (hexTerminalControl1.EchoBrush = new SolidBrush(_deviceTerminalSettings.Colors.Echo));
        HexMode = _deviceTerminalSettings.DisplayMode != DeviceTerminalSettings.CharacterDisplayMode.ASCII;
        echoTypedCharactersToolStripMenuItem.Checked = _deviceTerminalSettings.EchoTypedCharacters;
        _serialTerminalControl.LocalEcho = _deviceTerminalSettings.EchoTypedCharacters;
        if (_isHexMode)
        {
            hexTerminalControl1.HexSettings = deviceTerminalSettings.HexSettings;
            sendAsHEXToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.SendAsHex;
            sendImmediatelyToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.AutoSend;
        }
        changeSettingsToolStripMenuItem.Visible = _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial;
        UpdateTelnetMenu(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet);
        Connect();
    }

    private void Connect(bool resetTerminal = true, int value = 0)
    {
        // Any explicit connect (reconnect button, auto-reconnect, settings change) cancels a pending auto-reconnect
        // and is not treated as a manual disconnect anymore.
        _manualDisconnect = false;
        StopAutoReconnectTimer();
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Dispose();
            Thread.Sleep(300);
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
        _transportDisplayName = deviceTransport.GetDisplayName(includeParameters: true);
        lblConnection.Text = "Connected to " + _transportDisplayName;
        Interlocked.Exchange(ref _bytesSent, 0L);
        Interlocked.Exchange(ref _bytesReceived, 0L);
        _connectionState = ConnectionStateEnum.Connecting;
        _telnetTransport = null;
        if (deviceTransport is TelnetTransport telnetTransport && _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet telnetSettings)
        {
            AttachTelnet(telnetTransport, telnetSettings);
        }
        _asyncTransportConnection = new AsyncTransportConnection(deviceTransport, Dispatcher.CurrentDispatcher, value);
        _asyncTransportConnection.DataReceived += OnDataReceived;
        _asyncTransportConnection.StateChanged += OnConnectionStateChanged;
        _asyncTransportConnection.WriterIdleChanged += OnWriterIdleChanged;
        Size terminalSize = _serialTerminalControl.TerminalSize;
        _asyncTransportConnection.SetTerminalSize(terminalSize.Width, terminalSize.Height);
        _asyncTransportConnection.Start();
    }

    private void OnWriterIdleChanged(object sender, EventArgs e)
    {
    }

    private void SendToDevice(byte[] buffer, int count)
    {
        AsyncTransportConnection connection = _asyncTransportConnection;
        if (connection == null)
        {
            throw new InvalidOperationException("The connection is closed. Use Reconnect first.");
        }
        connection.Send(buffer, count);
        Interlocked.Add(ref _bytesSent, count);
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
        if (settings.ReconnectImmediately)
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
        _autoReconnectTimer.Interval = settings.ReconnectIntervalSeconds * 1000;
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
            Connect(resetTerminal: _deviceTerminalSettings.ClearContentsWhenReconnecting);
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
        try
        {
            ApplyConnectionState(connectionStateEnum, value);
            if (connectionStateEnum == ConnectionStateEnum.Connected)
            {
                _serialTerminalControl.BeginNewSession();
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
        if (connectionStateEnum == ConnectionStateEnum.Connecting || connectionStateEnum == ConnectionStateEnum.Failed)
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
                    string text = (lblConnecting.Text = "Connection closed");
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

    public static string TestConnection(DeviceTerminalSettings.ConnectionSettings connectionSettings)
    {
        using IDeviceTransport deviceTransport = new CallbackTransportFactory(CreateTransportCallback(connectionSettings) ?? throw new Exception("Connection type not specified")).CreateTransport();
        if (deviceTransport == null)
        {
            throw new Exception("Unspecified error");
        }
        deviceTransport.Open(null);
        return deviceTransport.GetDisplayName(includeParameters: true);
    }

    private void OnDataReceived(object sender, byte[] buffer, int value)
    {
        // The log gets the raw stream before it is split into text or hex, so recording does not depend on the display mode.
        if (_logWriter != null)
        {
            _logWriter.Write(buffer, 0, value);
            int now = Environment.TickCount;
            if (now - _lastLogButtonUpdate > 500)
            {
                _lastLogButtonUpdate = now;
                UpdateLogButton();
            }
        }
        if (_isHexMode)
        {
            hexTerminalControl1.AppendData(buffer, value);
            return;
        }
        string text = _textDecoder.Decode(buffer, 0, value);
        if (_deviceTerminalSettings != null && _deviceTerminalSettings.TreatLFAsCRLF)
        {
            text = text.Replace("\n", "\r\n");
        }
        _serialTerminalControl.OnDataReceived(text);
    }

    /// <summary>
    /// Feeds previously recorded received data (see <see cref="SmarTTY.Forms.LogReplayForm"/>) back into this terminal exactly like
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
        if (_isHexMode)
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
        _serialTerminalControl.OnDataReceived(text);
    }

    /// <summary>Resets the incremental decoder so a replay can safely restart from the beginning of a log.</summary>
    public void ResetReplayDecoder()
    {
        _textDecoder.Reset();
    }

    private void OnTerminalDataEntered(object sender, byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
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

    private static int HexDigitValue(char ch)
    {
        if (ch >= '0' && ch <= '9')
        {
            return ch - 48;
        }
        if (ch >= 'a' && ch <= 'f')
        {
            return ch - 97 + 10;
        }
        if (ch >= 'A' && ch <= 'F')
        {
            return ch - 65 + 10;
        }
        return -1;
    }

    private static byte[] ParseHexBytes(string text)
    {
        List<byte> list = new List<byte>();
        byte b = 0;
        int itemIndex = 0;
        char[] items = text.ToCharArray();
        for (int arrayIndex = 0; arrayIndex < items.Length; arrayIndex++)
        {
            char ch = items[arrayIndex];
            if (ch == ' ')
            {
                if (itemIndex > 0)
                {
                    list.Add(b);
                    itemIndex = 0;
                    b = 0;
                }
                continue;
            }
            int value = HexDigitValue(ch);
            if (value == -1)
            {
                throw new ArgumentException("Invalid HEX character: " + ch);
            }
            if (itemIndex >= 2)
            {
                list.Add(b);
                itemIndex = 0;
                b = 0;
            }
            b = (byte)((b << 4) | value);
            itemIndex++;
        }
        if (itemIndex > 0)
        {
            list.Add(b);
        }
        return list.ToArray();
    }

    private void btnSend_ButtonClick(object sender, EventArgs e)
    {
        if (!btnSend.Enabled)
        {
            return;
        }
        try
        {
            byte[] items = ((!sendAsHEXToolStripMenuItem.Checked) ? _textEncoding.GetBytes(txtHexInput.Text) : ParseHexBytes(txtHexInput.Text));
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
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
            string text = txtHexInput.Text.Trim();
            if (text.Length >= 2 || text.EndsWith(" "))
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
        if (sendAsHEXToolStripMenuItem.Checked ? HexDigitValue(e.KeyChar) != -1 : !char.IsControl(e.KeyChar))
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
        if (sendAsHEXToolStripMenuItem.Checked && HexDigitValue(e.KeyChar) == -1 && e.KeyChar != '\b' && e.KeyChar != ' ')
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
        _serialTerminalControl.Reset();
        hexTerminalControl1.Clear();
    }

    private void toolStripButton2_Click(object sender, EventArgs e)
    {
        if (_saveFileDialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }
        try
        {
            if (HexMode)
            {
                File.WriteAllBytes(_saveFileDialog.FileName, hexTerminalControl1.AllBytes);
            }
            else
            {
                File.WriteAllText(_saveFileDialog.FileName, _serialTerminalControl.Text);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void toolStripButton3_Click(object sender, EventArgs e)
    {
        if (_openFileDialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }
        try
        {
            byte[] items = File.ReadAllBytes(_openFileDialog.FileName);
            SendToDevice(items, items.Length);
            if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(items, items.Length, isEcho: true);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
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

    private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
    {
        try
        {
            string text = Win32Clipboard.GetText(Handle);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            byte[] bytes = _textEncoding.GetBytes(text);
            SendToDevice(bytes, bytes.Length);
            if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(bytes, bytes.Length, isEcho: true);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void btnReplay_Click(object sender, EventArgs e)
    {
        if (_logWriter != null)
        {
            MessageBox.Show("Stop recording first, then replay.", "Replay log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Play back a recorded log of received data",
            Filter = "All files (*.*)|*.*|Text log (*.log)|*.log|Hex dump (*.txt)|*.txt|Raw bytes (*.bin)|*.bin",
            FilterIndex = 1,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (bytes.Length == 0)
        {
            MessageBox.Show("The file is empty.", "Replay log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        new SmarTTY.Forms.LogReplayForm(bytes, this).Show();
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
        using SaveFileDialog dialog = new SaveFileDialog
        {
            Title = "Record received data (appended if the file already exists)",
            Filter = "Text log (UTF-8) (*.log)|*.log|Hex dump (*.txt)|*.txt|Raw bytes (*.bin)|*.bin",
            FilterIndex = HexMode ? 2 : 1,
            FileName = SuggestLogFileName(),
            AddExtension = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        TerminalLogFormat format = dialog.FilterIndex switch
        {
            2 => TerminalLogFormat.HexDump,
            3 => TerminalLogFormat.RawBytes,
            _ => TerminalLogFormat.Text,
        };
        try
        {
            TerminalLogWriter writer = new TerminalLogWriter(dialog.FileName, format, _textEncoding, append: true);
            writer.Failed += OnLogFailed;
            _logWriter = writer;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        btnLog.ForeColor = toolStrip1.ForeColor;
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
        btnLog.ForeColor = Color.Firebrick;
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
                    MessageBox.Show("Logging stopped: " + exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        HexMode = false;
    }

    private void cbHEX_Click(object sender, EventArgs e)
    {
        HexMode = true;
    }

    private void echoTypedCharactersToolStripMenuItem_Click(object sender, EventArgs e)
    {
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
        using SerialPortSettingsForm serialPortSettingsForm = new SerialPortSettingsForm(serial.AdvancedSettings, serial.ComPortName);
        if (serialPortSettingsForm.ShowModal() == DialogResult.OK)
        {
            serial.AdvancedSettings = serialPortSettingsForm.AdvancedSettings;
            Connect(resetTerminal: false, 1000);
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
        if (int.TryParse(text, out var baudRate) && _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial)
        {
            if (serial.AdvancedSettings == null)
            {
                serial.AdvancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings();
            }
            if (serial.AdvancedSettings.BaudRate != baudRate)
            {
                serial.AdvancedSettings.BaudRate = baudRate;
                Connect(resetTerminal: false, 1000);
            }
        }
    }

    private void cbBaudRate_Leave(object sender, EventArgs e)
    {
        ApplyBaudRate(cbBaudRate.Text);
    }
}
