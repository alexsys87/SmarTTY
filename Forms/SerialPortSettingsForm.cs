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
            cbDataBits.SelectedIndex = advancedSerialPortSettings.DataBits - 5;
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
            cbStopBits.SelectedIndex = (int)advancedSerialPortSettings.StopBits;
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
    }

    private void rbManual_CheckedChanged(object sender, EventArgs e)
    {
        panel1.Enabled = rbManual.Checked;
    }

    protected override void btnOK_Click(object sender, EventArgs e)
    {
        if (!int.TryParse(cbBitsPerSecond.Text, out var baudRate))
        {
            MessageBox.Show("Invalid baud rate. Should be an integer!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        _advancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings
        {
            BaudRate = baudRate,
            DataBits = 5 + cbDataBits.SelectedIndex,
            Parity = (Parity)cbParity.SelectedIndex,
            StopBits = (StopBits)cbStopBits.SelectedIndex,
            FlowControl = (Handshake)cbFlowControl.SelectedIndex
        };
        base.btnOK_Click(sender, e);
    }

    private void cbStopBits_SelectedIndexChanged(object sender, EventArgs e)
    {
        int bitsPerFrame = (5 + cbDataBits.SelectedIndex) * 2;
        bitsPerFrame += 2;
        if (cbParity.SelectedIndex != 0)
        {
            bitsPerFrame += 2;
        }
        switch (cbStopBits.SelectedIndex)
        {
        case 1:
            bitsPerFrame += 2;
            break;
        case 2:
            bitsPerFrame += 4;
            break;
        case 3:
            bitsPerFrame += 3;
            break;
        }
        if (!int.TryParse(cbBitsPerSecond.Text, out var baudRate))
        {
            label7.Text = "unknown";
            return;
        }
        double bytesPerSec = (double)(baudRate * 2) / (double)bitsPerFrame;
        if (bytesPerSec > 1048576.0)
        {
            label7.Text = $"{bytesPerSec / 1048576.0:f1} MB/s";
        }
        else if (bytesPerSec > 1024.0)
        {
            label7.Text = $"{bytesPerSec / 1024.0:f1} KB/s";
        }
        else
        {
            label7.Text = $"{bytesPerSec:f1} bytes/s";
        }
    }
}
