using System;
using System.IO.Ports;
using System.Windows;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.GUI;

/// <summary>
/// Lets the user keep the driver's serial port settings or pick them by hand (bits per second, frame, flow control).
/// </summary>
public partial class SerialPortSettingsWindow : Window
{
    private DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings _advancedSettings;

    /// <summary>The settings chosen by the user, or null to leave the port as the driver has it configured.</summary>
    public DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings AdvancedSettings => _advancedSettings;

    private SerialPortSettingsWindow(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSettings, string portName)
    {
        InitializeComponent();
        BaudRateBox.ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200", "230400", "460800", "921600", "1000000", "2000000" };
        DataBitsBox.ItemsSource = new[] { "5", "6", "7", "8" };
        ParityBox.ItemsSource = new[] { "None", "Odd", "Even", "Mark", "Space" };
        StopBitsBox.ItemsSource = new[] { "1", "1.5", "2" };
        FlowControlBox.ItemsSource = new[] { "None", "Software (XON/XOFF)", "Hardware (RTS/CTS)", "Both" };
        // The drop-down lists are fixed choices; only the bits-per-second box can be typed into.
        DataBitsBox.IsReadOnly = true;
        ParityBox.IsReadOnly = true;
        StopBitsBox.IsReadOnly = true;
        FlowControlBox.IsReadOnly = true;
        // An editable combo box does not raise SelectionChanged for typed text; the inner text box does.
        BaudRateBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new System.Windows.Controls.TextChangedEventHandler((sender, e) => UpdateSpeedText()));

        DefaultRadio.Content = "Use default settings for " + (string.IsNullOrEmpty(portName) ? "the selected COM port" : portName);
        if (advancedSettings == null)
        {
            DefaultRadio.IsChecked = true;
            LoadSettings(new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings());
        }
        else
        {
            ManualRadio.IsChecked = true;
            LoadSettings(advancedSettings);
        }
        UpdateSpeedText();
    }

    /// <summary>
    /// Shows the window. Returns whether it was accepted and, if so, the chosen settings; those are null when the
    /// user chose to keep the driver's settings.
    /// </summary>
    public static (bool Accepted, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings Settings) Prompt(Window owner, DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSettings, string portName)
    {
        SerialPortSettingsWindow window = new SerialPortSettingsWindow(advancedSettings, portName);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return (window.ShowDialog() == true, window.AdvancedSettings);
    }

    private void LoadSettings(DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSettings)
    {
        BaudRateBox.Text = advancedSettings.BaudRate.ToString();
        DataBitsBox.SelectedIndex = Clamp(advancedSettings.DataBits - SerialPortRules.MinDataBits, DataBitsBox.Items.Count);
        ParityBox.SelectedIndex = Clamp((int)advancedSettings.Parity, ParityBox.Items.Count);
        // The list is 1, 1.5, 2 in the order of SerialStopBitsKind.
        StopBitsBox.SelectedIndex = Clamp((int)SerialPortRules.StopBitsKindFrom((int)advancedSettings.StopBits), StopBitsBox.Items.Count);
        FlowControlBox.SelectedIndex = Clamp((int)advancedSettings.FlowControl, FlowControlBox.Items.Count);
        DtrBox.IsChecked = advancedSettings.DtrOnOpen;
        RtsBox.IsChecked = advancedSettings.RtsOnOpen;
    }

    private static int Clamp(int index, int count)
    {
        return index >= 0 && index < count ? index : -1;
    }

    private void ManualRadio_CheckedChanged(object sender, RoutedEventArgs e)
    {
        SettingsPanel.IsEnabled = ManualRadio.IsChecked == true;
        UpdateSpeedText();
    }

    private void Setting_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSpeedText();
    }

    private void UpdateSpeedText()
    {
        if (!int.TryParse(BaudRateBox.Text, out int baudRate) || baudRate <= 0
            || DataBitsBox.SelectedIndex < 0 || ParityBox.SelectedIndex < 0 || StopBitsBox.SelectedIndex < 0)
        {
            SpeedText.Text = "unknown";
            return;
        }
        double bytesPerSecond = SerialPortRules.BytesPerSecond(baudRate, SerialPortRules.MinDataBits + DataBitsBox.SelectedIndex,
            ParityBox.SelectedIndex != 0, (SerialStopBitsKind)StopBitsBox.SelectedIndex);
        SpeedText.Text = SerialPortRules.FormatSpeed(bytesPerSecond);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DefaultRadio.IsChecked == true)
        {
            // "Use default settings": leave the port as the driver has it configured.
            _advancedSettings = null;
            DialogResult = true;
            return;
        }
        if (!SerialPortRules.TryParseBaudRate(BaudRateBox.Text, out int baudRate, out string error))
        {
            MessageBoxes.Warning(error);
            BaudRateBox.Focus();
            return;
        }
        if (DataBitsBox.SelectedIndex < 0 || ParityBox.SelectedIndex < 0 || StopBitsBox.SelectedIndex < 0 || FlowControlBox.SelectedIndex < 0)
        {
            MessageBoxes.Warning("Choose the data bits, parity, stop bits and flow control.");
            return;
        }
        int dataBits = SerialPortRules.MinDataBits + DataBitsBox.SelectedIndex;
        SerialStopBitsKind stopBits = (SerialStopBitsKind)StopBitsBox.SelectedIndex;
        string frameProblem = SerialPortRules.ValidateFrame(dataBits, stopBits);
        if (frameProblem != null)
        {
            MessageBoxes.Warning(frameProblem);
            StopBitsBox.Focus();
            return;
        }
        _advancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings
        {
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = (Parity)ParityBox.SelectedIndex,
            StopBits = (StopBits)SerialPortRules.FrameworkStopBitsFrom(stopBits),
            FlowControl = (Handshake)FlowControlBox.SelectedIndex,
            DtrOnOpen = DtrBox.IsChecked == true,
            RtsOnOpen = RtsBox.IsChecked == true
        };
        DialogResult = true;
    }
}
