using SmarTTY.Forms;
using SmarTTY.Native;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

public partial class DeviceTerminalControl : DpiAwareControl
{

    private ITransportFactory _transportFactory;

    private AsyncTransportConnection _asyncTransportConnection;

    private DeviceTerminalSettings _deviceTerminalSettings;

    private Brush _brush;

    private bool _isHexMode;

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
            bool flag = (btnSend.Visible = value);
            bool visible = (toolStripTextBox.Visible = flag);
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
        TerminalEmulatorControl terminalEmulatorControl = terminalEmulatorControl1;
        Color backColor = (hexTerminalControl1.BackColor = deviceTerminalSettings.Colors.Background);
        terminalEmulatorControl.BackColor = backColor;
        TerminalEmulatorControl terminalEmulatorControl2 = terminalEmulatorControl1;
        backColor = (hexTerminalControl1.ForeColor = deviceTerminalSettings.Colors.Text);
        terminalEmulatorControl2.ForeColor = backColor;
        terminalEmulatorControl1.EnterSequence = GetLineEnding(deviceTerminalSettings.LineEnding);
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

    private void Connect(bool flag = true, int value2 = 0)
    {
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Dispose();
            Thread.Sleep(300);
        }
        _asyncTransportConnection = null;
        if (flag)
        {
            terminalEmulatorControl1.Reset();
        }
        IDeviceTransport deviceTransport = this._transportFactory.CreateTransport();
        lblConnection.Text = "Connected to " + deviceTransport.GetDisplayName(flag: true);
        _asyncTransportConnection = new AsyncTransportConnection(deviceTransport, Dispatcher.CurrentDispatcher, value2);
        _asyncTransportConnection.DataReceived += OnDataReceived;
        _asyncTransportConnection.StateChanged += OnConnectionStateChanged;
        _asyncTransportConnection.WriterIdleChanged += OnWriterIdleChanged;
        _asyncTransportConnection.Start();
    }

    private void OnWriterIdleChanged(object sender, EventArgs e)
    {
    }

    private void OnConnectionStateChanged(object obj, ConnectionStateEnum connectionStateEnum, string text2)
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
                lblConnecting.Text = text2;
                break;
            case ConnectionStateEnum.Disconnected:
                {
                    TerminalEmulatorControl terminalEmulatorControl = terminalEmulatorControl1;
                    bool flag = (hexTerminalControl1.HideCaret = true);
                    terminalEmulatorControl.HideCaret = flag;
                    TerminalEmulatorControl terminalEmulatorControl2 = terminalEmulatorControl1;
                    Color backColor = (hexTerminalControl1.BackColor = _deviceTerminalSettings.Colors.Disconnected);
                    terminalEmulatorControl2.BackColor = backColor;
                    ToolStripLabel toolStripLabel = lblConnection;
                    string text = (lblConnecting.Text = "Connection closed");
                    toolStripLabel.Text = text;
                    break;
                }
            case ConnectionStateEnum.Failed:
                lblConnecting.Text = "Connection failed: " + text2;
                break;
            case ConnectionStateEnum.Connected:
                {
                    TerminalEmulatorControl terminalEmulatorControl3 = terminalEmulatorControl1;
                    Color backColor = (hexTerminalControl1.BackColor = _deviceTerminalSettings.Colors.Background);
                    terminalEmulatorControl3.BackColor = backColor;
                    TerminalEmulatorControl terminalEmulatorControl4 = terminalEmulatorControl1;
                    bool flag = (hexTerminalControl1.HideCaret = false);
                    terminalEmulatorControl4.HideCaret = flag;
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
        return deviceTransport.GetDisplayName(flag: true);
    }

    private void OnDataReceived(object obj, byte[] buffer, int value2)
    {
        if (_isHexMode)
        {
            hexTerminalControl1.AppendData(buffer, value2);
            return;
        }
        string text = Encoding.ASCII.GetString(buffer, 0, value2);
        if (_deviceTerminalSettings != null && _deviceTerminalSettings.TreatLFAsCRLF)
        {
            text = text.Replace("\n", "\r\n");
        }
        terminalEmulatorControl1.ProcessOutput(text);
    }

    private void OnTerminalDataEntered(object obj, byte[] buffer, TerminalEmulatorControl.InputKind inputKind)
    {
        if (_asyncTransportConnection != null)
        {
            _asyncTransportConnection.Send(buffer, buffer.Length);
        }
        if (_deviceTerminalSettings.EchoTypedCharacters && inputKind != TerminalEmulatorControl.InputKind.MouseReport && !_isHexMode)
        {
            TerminalEmulatorBase.CellStyle cellStyle = terminalEmulatorControl1.CurrentStyle;
            TerminalEmulatorBase.CellStyle cellStyle2 = cellStyle;
            cellStyle2.Override = new TerminalEmulatorBase.CellStyle.ForegroundOverride
            {
                Brush = _brush
            };
            terminalEmulatorControl1.CurrentStyle = cellStyle2;
            string text = Encoding.ASCII.GetString(buffer);
            switch (text)
            {
                case "\r":
                case "\n":
                case "\r\n":
                    terminalEmulatorControl1.MoveCursorBy(-terminalEmulatorControl1.CursorPosition.X, 1, flag: true);
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
        int num = 0;
        char[] array = text.ToCharArray();
        for (int num2 = 0; num2 < array.Length; num2++)
        {
            char ch = array[num2];
            if (ch == ' ')
            {
                if (num > 0)
                {
                    list.Add(b);
                    num = 0;
                    b = 0;
                }
                continue;
            }
            int num3 = HexDigitValue(ch);
            if (num3 == -1)
            {
                throw new ArgumentException("Invalid HEX character: " + ch);
            }
            if (num >= 2)
            {
                list.Add(b);
                num = 0;
                b = 0;
            }
            b = (byte)((b << 4) | num3);
            num++;
        }
        if (num > 0)
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
            byte[] array = ((!sendAsHEXToolStripMenuItem.Checked) ? Encoding.ASCII.GetBytes(txtHexInput.Text) : ParseHexBytes(txtHexInput.Text));
            _asyncTransportConnection.Send(array, array.Length);
            if (_deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(array, array.Length, flag: true);
            }
            if ((Control.ModifierKeys & Keys.Alt) == 0)
            {
                txtHexInput.Text = "";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        if (e.KeyChar >= ' ' && e.KeyChar <= 'z' && (!sendAsHEXToolStripMenuItem.Checked || HexDigitValue(e.KeyChar) != -1))
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
            byte[] array = File.ReadAllBytes(_openFileDialog.FileName);
            _asyncTransportConnection.Send(array, array.Length);
            if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(array, array.Length, flag: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

    private void pasteToolStripMenuItem_Click(object sender, EventArgs e)
    {
        try
        {
            string text = Win32Clipboard.GetText(Handle);
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            _asyncTransportConnection.Send(bytes, bytes.Length);
            if (HexMode && _deviceTerminalSettings.EchoTypedCharacters)
            {
                hexTerminalControl1.AppendData(bytes, bytes.Length, flag: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

    private void changeSettingsToolStripMenuItem_Click(object sender, EventArgs e2)
    {
        if (!(_deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial))
        {
            return;
        }
        using SerialPortSettingsForm serialPortSettingsForm = new SerialPortSettingsForm(serial.AdvancedSettings, serial.ComPortName);
        if (serialPortSettingsForm.ShowModal() == DialogResult.OK)
        {
            serial.AdvancedSettings = serialPortSettingsForm.AdvancedSettings;
            Connect(flag: false, 1000);
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
        if (int.TryParse(text, out var result) && _deviceTerminalSettings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial)
        {
            if (serial.AdvancedSettings == null)
            {
                serial.AdvancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings();
            }
            if (serial.AdvancedSettings.BaudRate != result)
            {
                serial.AdvancedSettings.BaudRate = result;
                Connect(flag: false, 1000);
            }
        }
    }

    private void cbBaudRate_Leave(object sender, EventArgs e)
    {
        ApplyBaudRate(cbBaudRate.Text);
    }
}
