using System;
using System.IO.Ports;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Forms;

public partial class SerialPortSettingsForm : OKCancelFormBase
{
    private DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings _advancedSettings;

    public DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings AdvancedSettings => _advancedSettings;

    public SerialPortSettingsForm(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings, string portName)
    {
        InitializeComponent();
        FormIcons.Apply(this, "serial-settings");
        if (string.IsNullOrEmpty(portName))
        {
            portName = "the selected COM port";
        }
        rbDefault.Text += portName;
        if (advancedSerialPortSettings == null)
        {
            rbDefault.Checked = true;
            LoadSettings(new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings());
        }
        else
        {
            rbManual.Checked = true;
            LoadSettings(advancedSerialPortSettings);
        }
    }

    private void LoadSettings(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings)
    {
        cbBitsPerSecond.Text = advancedSerialPortSettings.BaudRate.ToString();
        try
        {
            cbDataBits.SelectedIndex = advancedSerialPortSettings.DataBits - SerialPortRules.MinDataBits;
        }
        catch
        {
        }
        try
        {
            cbParity.SelectedIndex = (int)advancedSerialPortSettings.Parity;
        }
        catch
        {
        }
        try
        {
            // The list is 1, 1.5, 2 in the order of SerialStopBitsKind.
            cbStopBits.SelectedIndex = (int)SerialPortRules.StopBitsKindFrom((int)advancedSerialPortSettings.StopBits);
        }
        catch
        {
        }
        try
        {
            cbFlowControl.SelectedIndex = (int)advancedSerialPortSettings.FlowControl;
        }
        catch
        {
        }
        cbDtrOnOpen.Checked = advancedSerialPortSettings.DtrOnOpen;
        cbRtsOnOpen.Checked = advancedSerialPortSettings.RtsOnOpen;
    }

    private void rbManual_CheckedChanged(object sender, EventArgs e)
    {
        panel1.Enabled = rbManual.Checked;
    }

    protected override void btnOK_Click(object sender, EventArgs e)
    {
        if (rbDefault.Checked)
        {
            // "Use default settings": leave the port as the driver has it configured.
            _advancedSettings = null;
            base.btnOK_Click(sender, e);
            return;
        }
        if (!SerialPortRules.TryParseBaudRate(cbBitsPerSecond.Text, out int baudRate, out string error))
        {
            MessageBox.Show(error, "Serial port settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            cbBitsPerSecond.Focus();
            return;
        }
        if (cbDataBits.SelectedIndex < 0 || cbParity.SelectedIndex < 0 || cbStopBits.SelectedIndex < 0 || cbFlowControl.SelectedIndex < 0)
        {
            MessageBox.Show("Choose the data bits, parity, stop bits and flow control.", "Serial port settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int dataBits = SerialPortRules.MinDataBits + cbDataBits.SelectedIndex;
        SerialStopBitsKind stopBits = (SerialStopBitsKind)cbStopBits.SelectedIndex;
        string frameProblem = SerialPortRules.ValidateFrame(dataBits, stopBits);
        if (frameProblem != null)
        {
            MessageBox.Show(frameProblem, "Serial port settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            cbStopBits.Focus();
            return;
        }
        _advancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings
        {
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = (Parity)cbParity.SelectedIndex,
            StopBits = (StopBits)SerialPortRules.FrameworkStopBitsFrom(stopBits),
            FlowControl = (Handshake)cbFlowControl.SelectedIndex,
            DtrOnOpen = cbDtrOnOpen.Checked,
            RtsOnOpen = cbRtsOnOpen.Checked
        };
        base.btnOK_Click(sender, e);
    }

    private void cbStopBits_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (!int.TryParse(cbBitsPerSecond.Text, out int baudRate) || baudRate <= 0
            || cbDataBits.SelectedIndex < 0 || cbParity.SelectedIndex < 0 || cbStopBits.SelectedIndex < 0)
        {
            label7.Text = "unknown";
            return;
        }
        double bytesPerSecond = SerialPortRules.BytesPerSecond(baudRate, SerialPortRules.MinDataBits + cbDataBits.SelectedIndex,
            cbParity.SelectedIndex != 0, (SerialStopBitsKind)cbStopBits.SelectedIndex);
        label7.Text = SerialPortRules.FormatSpeed(bytesPerSecond);
    }
}
