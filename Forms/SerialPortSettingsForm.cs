using System;
using System.IO.Ports;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Forms;

public partial class SerialPortSettingsForm : OKCancelFormBase
{
    private DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings _advancedSettings;

    public DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings AdvancedSettings => _advancedSettings;

    public SerialPortSettingsForm(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings, string text)
    {
        InitializeComponent();
        if (string.IsNullOrEmpty(text))
        {
            text = "the selected COM port";
        }
        rbDefault.Text += text;
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

    protected override void btnOK_Click(object sender, EventArgs e2)
    {
        if (!int.TryParse(cbBitsPerSecond.Text, out var result))
        {
            MessageBox.Show("Invalid baud rate. Should be an integer!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        _advancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings
        {
            BaudRate = result,
            DataBits = 5 + cbDataBits.SelectedIndex,
            Parity = (Parity)cbParity.SelectedIndex,
            StopBits = (StopBits)cbStopBits.SelectedIndex,
            FlowControl = (Handshake)cbFlowControl.SelectedIndex
        };
        base.btnOK_Click(sender, e2);
    }

    private void cbStopBits_SelectedIndexChanged(object sender, EventArgs e2)
    {
        int num = (5 + cbDataBits.SelectedIndex) * 2;
        num += 2;
        if (cbParity.SelectedIndex != 0)
        {
            num += 2;
        }
        switch (cbStopBits.SelectedIndex)
        {
        case 1:
            num += 2;
            break;
        case 2:
            num += 4;
            break;
        case 3:
            num += 3;
            break;
        }
        if (!int.TryParse(cbBitsPerSecond.Text, out var result))
        {
            label7.Text = "unknown";
            return;
        }
        double num2 = (double)(result * 2) / (double)num;
        if (num2 > 1048576.0)
        {
            label7.Text = $"{num2 / 1048576.0:f1} MB/s";
        }
        else if (num2 > 1024.0)
        {
            label7.Text = $"{num2 / 1024.0:f1} KB/s";
        }
        else
        {
            label7.Text = $"{num2:f1} bytes/s";
        }
    }
}
