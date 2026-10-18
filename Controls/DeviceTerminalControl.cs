using SmarTTY.Forms;
using SmarTTY.Native;
using SmarTTY.Terminal;
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

    private long _bytesSent;

    private long _bytesReceived;

    private ConnectionStateEnum _connectionState = ConnectionStateEnum.Connecting;

    /// <summary>Raised on the UI thread whenever the transport state or the displayed title changes.</summary>
    public event EventHandler StatusChanged;

    /// <summary>
    /// The text terminal while it has the keyboard focus (null in HEX mode or when the focus is in the toolbar), so
    /// window-level key handling (F10, Alt+letter) never leaks into the baud rate box or the "Send" field.
    /// </summary>
    internal TerminalEmulatorControl FocusedTextTerminal => (!_isHexMode && terminalEmulatorControl1.Focused) ? terminalEmulatorControl1 : null;

    /// <summary>Raised when the user presses Ctrl+W and the shared "Close tab on Ctrl+W" option is on.</summary>
    public event EventHandler CloseTabRequested;

    /// <summary>Raised when the user presses Alt+Enter (full screen on/off, as in SSH tabs).</summary>
    public event EventHandler FullScreenToggleRequested;

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
            terminalEmulatorControl1.Visible = !value;
            cbASCII.Checked = !value;
            cbHEX.Checked = value;
            ToolStripLabel toolStripLabel = lblSend;
            ToolStripTextBox toolStripTextBox = txtHexInput;
            bool isVisible = (btnSend.Visible = value);
            bool visible = (toolStripTextBox.Visible = isVisible);
            toolStripLabel.Visible = visible;
        }
    }

    public DeviceTerminalControl()
    {
        InitializeComponent();
        DpiAwareForm.ConfigureToolStrip(toolStrip1);
        Disposed += DeviceTerminalControl_Disposed;
        pnlConnectionInProgress.BringToFront();
        toolStrip1.SendToBack();
        foreach (TerminalEncodings.Choice choice in TerminalEncodings.All)
        {
            cbEncoding.Items.Add(choice);
        }
        SelectEncodingInToolbar(TerminalEncodings.DefaultName);
    }

    private void SetTextEncoding(string encodingName)
    {
        _textEncoding = TerminalEncodings.Resolve(encodingName);
        _textDecoder = new TextStreamDecoder(_textEncoding);
        terminalEmulatorControl1.InputEncoding = _textEncoding;
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
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Dispose();
        }
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
        TerminalEmulatorControl terminal = terminalEmulatorControl1;
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
        TerminalEmulatorControl terminal = terminalEmulatorControl1;
        Color backColor = (hexTerminalControl1.BackColor = deviceTerminalSettings.Colors.Background);
        terminal.BackColor = backColor;
        backColor = (hexTerminalControl1.ForeColor = deviceTerminalSettings.Colors.Text);
        terminal.ForeColor = backColor;
        terminalEmulatorControl1.EnterSequence = GetLineEnding(deviceTerminalSettings.LineEnding);
        SetTextEncoding(deviceTerminalSettings.TextEncoding);
        SelectEncodingInToolbar(deviceTerminalSettings.TextEncoding);
        _brush = (hexTerminalControl1.EchoBrush = new SolidBrush(_deviceTerminalSettings.Colors.Echo));
        HexMode = _deviceTerminalSettings.DisplayMode != DeviceTerminalSettings.CharacterDisplayMode.ASCII;
        echoTypedCharactersToolStripMenuItem.Checked = _deviceTerminalSettings.EchoTypedCharacters;
        if (_isHexMode)
        {
            hexTerminalControl1.HexSettings = deviceTerminalSettings.HexSettings;
            sendAsHEXToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.SendAsHex;
            sendImmediatelyToolStripMenuItem.Checked = deviceTerminalSettings.HexSettings.AutoSend;
        }
        changeSettingsToolStripMenuItem.Visible = _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial;
        Connect();
    }

    private void Connect(bool resetTerminal = true, int value = 0)
    {
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Dispose();
            Thread.Sleep(300);
        }
        _asyncTransportConnection = null;
        if (resetTerminal)
        {
            terminalEmulatorControl1.Reset();
        }
        // A half-received character from the previous connection must not be glued to the new data.
        _textDecoder.Reset();
        IDeviceTransport deviceTransport = this._transportFactory.CreateTransport();
        _transportDisplayName = deviceTransport.GetDisplayName(includeParameters: true);
        lblConnection.Text = "Connected to " + _transportDisplayName;
        Interlocked.Exchange(ref _bytesSent, 0L);
        Interlocked.Exchange(ref _bytesReceived, 0L);
        _connectionState = ConnectionStateEnum.Connecting;
        _asyncTransportConnection = new AsyncTransportConnection(deviceTransport, Dispatcher.CurrentDispatcher, value);
        _asyncTransportConnection.DataReceived += OnDataReceived;
        _asyncTransportConnection.StateChanged += OnConnectionStateChanged;
        _asyncTransportConnection.WriterIdleChanged += OnWriterIdleChanged;
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
            terminalEmulatorControl1.Focus();
        }
    }

    private void OnConnectionStateChanged(object sender, ConnectionStateEnum connectionStateEnum, string value)
    {
        _connectionState = connectionStateEnum;
        try
        {
            ApplyConnectionState(connectionStateEnum, value);
        }
        finally
        {
            RaiseStatusChanged();
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
                    TerminalEmulatorControl terminal = terminalEmulatorControl1;
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
                    TerminalEmulatorControl terminal = terminalEmulatorControl1;
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
        Interlocked.Add(ref _bytesReceived, value);
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
        terminalEmulatorControl1.ProcessOutput(text);
    }

    private void OnTerminalDataEntered(object sender, byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
        if (_asyncTransportConnection != null)
        {
            SendToDevice(buffer, buffer.Length);
        }
        if (_deviceTerminalSettings.EchoTypedCharacters && inputKind != TerminalEmulatorControl.InputKind.MouseReport && !_isHexMode)
        {
            TerminalEmulatorBase.CellStyle cellStyle = terminalEmulatorControl1.CurrentStyle;
            TerminalEmulatorBase.CellStyle overrideStyle = cellStyle;
            overrideStyle.Override = new TerminalEmulatorBase.CellStyle.ForegroundOverride
            {
                Brush = _brush
            };
            terminalEmulatorControl1.CurrentStyle = overrideStyle;
            string text = _textEncoding.GetString(buffer);
            switch (text)
            {
                case "\r":
                case "\n":
                case "\r\n":
                    terminalEmulatorControl1.MoveCursorBy(-terminalEmulatorControl1.CursorPosition.X, 1, scroll: true);
                    break;
                default:
                    terminalEmulatorControl1.WriteText(text);
                    break;
            }
            terminalEmulatorControl1.CurrentStyle = cellStyle;
        }
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
        terminalEmulatorControl1.Reset();
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
                File.WriteAllText(_saveFileDialog.FileName, terminalEmulatorControl1.Text);
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
            Win32Clipboard.SetText(Handle, terminalEmulatorControl1.Text);
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
        else if (((ReadonlyTextBrowser)terminalEmulatorControl1).HasSelection)
        {
            terminalEmulatorControl1.CopySelection(select: false);
        }
    }

    private void contextMenuStrip1_Opening(object sender, System.ComponentModel.CancelEventArgs e)
    {
        copyToolStripMenuItem.Enabled = HexMode ? hexTerminalControl1.HasSelection : ((ReadonlyTextBrowser)terminalEmulatorControl1).HasSelection;
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
    }

    public void Disconnect(string text = null)
    {
        AsyncTransportConnection asyncTransportConnection = _asyncTransportConnection;
        if (asyncTransportConnection != null && asyncTransportConnection.MatchesPort(text))
        {
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
