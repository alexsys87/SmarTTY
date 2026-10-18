using SmarTTY.Controls;
using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using VisualTTY.DeviceTerminal;
using Binding = System.Windows.Data.Binding;

namespace SmarTTY.GUI;

public partial class RawConnectionSettingsWindow : Window, IComponentConnector, IStyleConnector
{
    private class ControllerImpl : INotifyPropertyChanged
    {
        private string _TCPEndpoint;

        private string _MaximumSpeed;

        private EffectiveDisplayMode _EffectiveDisplayMode;

        public DeviceTerminalSettings.ConnectionSettings.Serial SerialSettings => Settings.Connection as DeviceTerminalSettings.ConnectionSettings.Serial;

        public DeviceTerminalSettings Settings { get; set; } = new DeviceTerminalSettings();

        public Visibility SaveCheckboxVisibility { get; }

        public string TCPEndpoint
        {
            get
            {
                return _TCPEndpoint;
            }
            set
            {
                _TCPEndpoint = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TCPEndpoint"));
                OnSettingsChanged();
            }
        }

        public bool SaveSettings { get; set; } = true;

        public DeviceTerminalSettings.LineEndingType[] LineEndings { get; } = new DeviceTerminalSettings.LineEndingType[3]
        {
            DeviceTerminalSettings.LineEndingType.CR,
            DeviceTerminalSettings.LineEndingType.LF,
            DeviceTerminalSettings.LineEndingType.CRLF
        };

        public System.Collections.Generic.IReadOnlyList<TerminalEncodings.Choice> TextEncodings => TerminalEncodings.All;

        public DeviceTerminalSettings.CharacterDisplayMode[] DisplayModes { get; } = new DeviceTerminalSettings.CharacterDisplayMode[2]
        {
            DeviceTerminalSettings.CharacterDisplayMode.ASCII,
            DeviceTerminalSettings.CharacterDisplayMode.HEX
        };

        public string MaximumSpeed
        {
            get
            {
                return _MaximumSpeed;
            }
            set
            {
                _MaximumSpeed = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("MaximumSpeed"));
            }
        }

        public bool HexModeEnabled => EffectiveDisplayMode != EffectiveDisplayMode.Text;

        public EffectiveDisplayMode EffectiveDisplayMode
        {
            get
            {
                return _EffectiveDisplayMode;
            }
            set
            {
                _EffectiveDisplayMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("EffectiveDisplayMode"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HexModeEnabled"));
            }
        }

        public DeviceTerminalSettings.ConnectionSettings EffectiveConnectionSettings
        {
            get
            {
                if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial)
                {
                    return SerialSettings;
                }
                string endpoint = TCPEndpoint ?? "";
                int colonIdx = endpoint.IndexOf(':');
                string host;
                int port;
                if (colonIdx == -1)
                {
                    host = endpoint;
                    port = 0;
                }
                else
                {
                    host = endpoint.Substring(0, colonIdx);
                    port = int.Parse(endpoint.Substring(colonIdx + 1));
                }
                return new DeviceTerminalSettings.ConnectionSettings.Tcp
                {
                    Host = host,
                    Port = port
                };
            }
        }

        public string SerialSettingsHeader { get; }

        public Visibility SerialSettingsVisibility { get; }

        public Visibility TCPSettingsVisibility { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public ControllerImpl(DeviceTerminalSettings settings, bool showSaveCheckbox)
        {
            Settings = settings.Clone();
            SaveCheckboxVisibility = ((!showSaveCheckbox) ? Visibility.Collapsed : Visibility.Visible);
            if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serial)
            {
                SerialSettingsHeader = serial.ComPortName + " settings";
                SerialSettingsVisibility = Visibility.Visible;
                TCPSettingsVisibility = Visibility.Collapsed;
            }
            else if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
            {
                SerialSettingsVisibility = Visibility.Collapsed;
                TCPSettingsVisibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(tcp.Host))
                {
                    TCPEndpoint = tcp.Host + ":" + tcp.Port;
                }
            }
        }

        public void UpdateMaximumSpeed()
        {
            OnSettingsChanged();
            DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings = SerialSettings?.AdvancedSettings;
            if (advancedSerialPortSettings == null)
            {
                MaximumSpeed = "unknown";
                return;
            }
            int bitsPerFrame = (5 + advancedSerialPortSettings.DataBits) * 2;
            bitsPerFrame += 2;
            if (advancedSerialPortSettings.Parity != Parity.None)
            {
                bitsPerFrame += 2;
            }
            switch (advancedSerialPortSettings.StopBits)
            {
                case StopBits.One:
                    bitsPerFrame += 2;
                    break;
                case StopBits.Two:
                    bitsPerFrame += 4;
                    break;
                case StopBits.OnePointFive:
                    bitsPerFrame += 3;
                    break;
            }
            double bytesPerSec = (double)(advancedSerialPortSettings.BaudRate * 2) / (double)bitsPerFrame;
            if (bytesPerSec > 1048576.0)
            {
                MaximumSpeed = $"{bytesPerSec / 1048576.0:f1} MB/s";
            }
            else if (bytesPerSec > 1024.0)
            {
                MaximumSpeed = $"{bytesPerSec / 1024.0:f1} KB/s";
            }
            else
            {
                MaximumSpeed = $"{bytesPerSec:f1} bytes/s";
            }
        }

        public void OnSettingsChanged()
        {
        }

        public string TestConnectionOrThrow()
        {
            ApplyTCPEndpoint();
            return DeviceTerminalControl.TestConnection(EffectiveConnectionSettings);
        }

        public void EditColor(Border border)
        {
            BindingExpression bindingExpression = ((FrameworkElement)border).GetBindingExpression(Border.BackgroundProperty);
            object pathObj;
            if (bindingExpression == null)
            {
                pathObj = null;
            }
            else
            {
                Binding parentBinding = bindingExpression.ParentBinding;
                if (parentBinding == null)
                {
                    pathObj = null;
                }
                else
                {
                    PropertyPath path = parentBinding.Path;
                    pathObj = ((path != null) ? path.Path : null);
                }
            }
            string pathStr = (string)pathObj;
            object dataContext = ((FrameworkElement)border).DataContext;
            if (pathStr == null || dataContext == null)
            {
                return;
            }
            string[] pathParts = pathStr.Split('.');
            if (pathParts.Length != 2)
            {
                return;
            }
            PropertyInfo objProp = dataContext.GetType().GetProperty(pathParts[0]);
            if (objProp == null)
            {
                return;
            }
            object containerObj = objProp.GetValue(dataContext, new object[0]);
            PropertyInfo colorProp = containerObj.GetType().GetProperty(pathParts[1]);
            if (colorProp != null && colorProp.GetValue(containerObj, new object[0]) is DeviceTerminalSettings.ColorWrapper colorWrapper)
            {
                ColorDialog colorDialog = new ColorDialog();
                colorDialog.Color = colorWrapper;
                if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    DeviceTerminalSettings.ColorWrapper newColorWrapper = colorDialog.Color;
                    colorProp.SetValue(containerObj, newColorWrapper, new object[0]);
                    objProp.SetValue(dataContext, containerObj, new object[0]);
                    ((BindingExpressionBase)bindingExpression).UpdateTarget();
                    OnSettingsChanged();
                }
            }
        }

        public void ApplyTCPEndpoint()
        {
            if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp)
            {
                Settings.Connection = EffectiveConnectionSettings;
            }
        }
    }

    private readonly ControllerImpl _Controller;

    private bool _TestOnOK;

    public DeviceTerminalSettings Settings => _Controller.Settings;

    public bool SaveSettings => _Controller.SaveSettings;

    public RawConnectionSettingsWindow(DeviceTerminalSettings settings, bool testOnOK, bool showSaveCheckbox)
    {
        InitializeComponent();
        this.DataContext = (_Controller = new ControllerImpl(settings, showSaveCheckbox));
        _TestOnOK = testOnOK;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _Controller.ApplyTCPEndpoint();
    }

    private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _Controller.UpdateMaximumSpeed();
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = _Controller.TestConnectionOrThrow();
            MessageBoxes.Info("Connection test to " + text + " succeeded. Your settings appear to be valid.");
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Border border = sender as Border;
        if (border != null)
        {
            _Controller.EditColor(border);
        }
    }

    private void RaiseSettingsChangedEvent(object sender, RoutedEventArgs e)
    {
        _Controller?.OnSettingsChanged();
    }

    private void ComboBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _Controller.UpdateMaximumSpeed();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (_TestOnOK)
        {
            try
            {
                _Controller.TestConnectionOrThrow();
                this.DialogResult = true;
                return;
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
                return;
            }
        }
        this.DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }
}
