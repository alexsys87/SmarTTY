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
                if (_EffectiveDisplayMode == value)
                {
                    return;
                }
                _EffectiveDisplayMode = value;
                ApplyDisplayModeToSettings(value);
                OnSettingsChanged();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("EffectiveDisplayMode"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HexModeEnabled"));
            }
        }

        /// <summary>Writes the selected mode into the settings that are returned to the caller on OK.</summary>
        private void ApplyDisplayModeToSettings(EffectiveDisplayMode mode)
        {
            if (Settings.HexSettings == null)
            {
                Settings.HexSettings = new DeviceTerminalSettings.HexTerminalSettings();
            }
            switch (mode)
            {
                case EffectiveDisplayMode.Text:
                    Settings.DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.ASCII;
                    Settings.HexSettings.ShowTextView = false;
                    break;
                case EffectiveDisplayMode.Hex:
                    Settings.DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.HEX;
                    Settings.HexSettings.ShowTextView = false;
                    break;
                case EffectiveDisplayMode.Both:
                    Settings.DisplayMode = DeviceTerminalSettings.CharacterDisplayMode.Both;
                    Settings.HexSettings.ShowTextView = true;
                    break;
            }
        }

        private static EffectiveDisplayMode ToEffectiveDisplayMode(DeviceTerminalSettings settings)
        {
            DeviceTerminalSettings.CharacterDisplayMode mode = DeviceTerminalSettings.CharacterDisplayModeHelper.Resolve(settings.DisplayMode, settings.HexSettings);
            if (mode == DeviceTerminalSettings.CharacterDisplayMode.Both)
            {
                return EffectiveDisplayMode.Both;
            }
            if (mode == DeviceTerminalSettings.CharacterDisplayMode.HEX)
            {
                return EffectiveDisplayMode.Hex;
            }
            return EffectiveDisplayMode.Text;
        }

        public bool IsTelnet => Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet;

        public class TelnetLocalEchoChoice
        {
            public DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode Value { get; set; }

            public string Text { get; set; }
        }

        public System.Collections.Generic.IReadOnlyList<TelnetLocalEchoChoice> TelnetLocalEchoChoices { get; } = new TelnetLocalEchoChoice[]
        {
            new TelnetLocalEchoChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Auto, Text = "Automatic (until the server echoes)" },
            new TelnetLocalEchoChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Always, Text = "Always on" },
            new TelnetLocalEchoChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode.Never, Text = "Always off" }
        };

        // The telnet options live here while the window is open; the connection object is rebuilt from the
        // endpoint text (EffectiveConnectionSettings), so they are copied into it every time.
        public string TelnetTerminalType { get; set; } = DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTerminalType;

        public DeviceTerminalSettings.ConnectionSettings.Telnet.LocalEchoMode TelnetLocalEcho { get; set; }

        public bool TelnetActiveNegotiation { get; set; }

        public bool TelnetLineMode { get; set; }

        public string TelnetTerminalSpeed { get; set; } = DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTerminalSpeed;

        public string TelnetXDisplayLocation { get; set; }

        public string TelnetEnvironmentVariables { get; set; }

        public bool TelnetTls { get; set; }

        public bool TelnetTlsAllowInvalidCertificate { get; set; }

        private int _TelnetKeepAliveSeconds;

        public int TelnetKeepAliveSeconds
        {
            get
            {
                return _TelnetKeepAliveSeconds;
            }
            set
            {
                _TelnetKeepAliveSeconds = Math.Max(0, Math.Min(value, 86400));
            }
        }

        private int _TcpKeepAliveSeconds;

        // Keep-alive of a raw TCP connection (the telnet one lives in the "Telnet options" box). It is hidden for
        // telnet so the two keep-alive inputs never both apply to the same connection.
        public int TcpKeepAliveSeconds
        {
            get
            {
                return _TcpKeepAliveSeconds;
            }
            set
            {
                _TcpKeepAliveSeconds = Math.Max(0, Math.Min(value, 86400));
            }
        }

        public Visibility TcpKeepAliveSettingsVisibility => IsTelnet ? Visibility.Collapsed : Visibility.Visible;

        public Visibility TelnetSettingsVisibility => IsTelnet ? Visibility.Visible : Visibility.Collapsed;

        // Automatic login. The password is kept here in plain text only while the window is open; it is encrypted
        // (like the saved SSH passwords) when the settings are built.
        public string TelnetAutoLoginUserName { get; set; }

        public string TelnetLoginPrompts { get; set; }

        public string TelnetPasswordPrompts { get; set; }

        public string TelnetPassword { get; set; }

        // Encrypting is slow (a key container) and the settings are built several times (test, OK, close): the
        // result is reused until the password changes.
        private string _EncryptedFor;

        private byte[] _EncryptedPassword;

        private byte[] EncryptTelnetPassword()
        {
            if (string.IsNullOrEmpty(TelnetPassword))
            {
                return null;
            }
            if (_EncryptedFor != TelnetPassword)
            {
                try
                {
                    _EncryptedPassword = VisualTTY.PasswordProtector.Encrypt(TelnetPassword, "com.sysprogs.smartty.passwords");
                    _EncryptedFor = TelnetPassword;
                }
                catch (Exception)
                {
                    // Too long or no key container: the connection is saved without a password rather than
                    // failing while the window closes.
                    _EncryptedPassword = null;
                    _EncryptedFor = TelnetPassword;
                }
            }
            return _EncryptedPassword;
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
                    // A telnet server is expected on the telnet port unless another one is given.
                    port = IsTelnet ? (TelnetTls ? DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTlsPort : DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultPort) : 0;
                }
                else
                {
                    host = endpoint.Substring(0, colonIdx);
                    port = int.Parse(endpoint.Substring(colonIdx + 1));
                }
                if (IsTelnet)
                {
                    return new DeviceTerminalSettings.ConnectionSettings.Telnet
                    {
                        Host = host,
                        Port = port,
                        TerminalType = string.IsNullOrWhiteSpace(TelnetTerminalType) ? DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTerminalType : TelnetTerminalType.Trim(),
                        LocalEcho = TelnetLocalEcho,
                        ActiveNegotiation = TelnetActiveNegotiation,
                        LineMode = TelnetLineMode,
                        TerminalSpeed = string.IsNullOrWhiteSpace(TelnetTerminalSpeed) ? null : TelnetTerminalSpeed.Trim(),
                        XDisplayLocation = string.IsNullOrWhiteSpace(TelnetXDisplayLocation) ? null : TelnetXDisplayLocation.Trim(),
                        EnvironmentVariables = string.IsNullOrWhiteSpace(TelnetEnvironmentVariables) ? null : TelnetEnvironmentVariables.Trim(),
                        Tls = TelnetTls,
                        TlsAllowInvalidCertificate = TelnetTls && TelnetTlsAllowInvalidCertificate,
                        KeepAliveSeconds = TelnetKeepAliveSeconds,
                        AutoLoginUserName = string.IsNullOrWhiteSpace(TelnetAutoLoginUserName) ? null : TelnetAutoLoginUserName.Trim(),
                        EncryptedSavedPassword = EncryptTelnetPassword(),
                        LoginPrompts = string.IsNullOrWhiteSpace(TelnetLoginPrompts) ? null : TelnetLoginPrompts.Trim(),
                        PasswordPrompts = string.IsNullOrWhiteSpace(TelnetPasswordPrompts) ? null : TelnetPasswordPrompts.Trim()
                    };
                }
                return new DeviceTerminalSettings.ConnectionSettings.Tcp
                {
                    Host = host,
                    Port = port,
                    KeepAliveSeconds = TcpKeepAliveSeconds
                };
            }
        }

        public string TCPSettingsHeader => IsTelnet
            ? "Telnet connection settings (default port " + DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultPort + ", with TLS " + DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTlsPort + ")"
            : "Raw TCP connection settings";

        public string SerialSettingsHeader { get; }

        public Visibility SerialSettingsVisibility { get; }

        public Visibility TCPSettingsVisibility { get; }

        // Auto-reconnect options, forwarded to the root settings so that DeviceTerminalControl can read them back
        // when the connection is opened. Raised through the controller so the WPF bindings update.
        public bool ReconnectAutomatically
        {
            get
            {
                return Settings.ReconnectAutomatically;
            }
            set
            {
                Settings.ReconnectAutomatically = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReconnectAutomatically"));
            }
        }

        public bool ReconnectImmediately
        {
            get
            {
                return Settings.ReconnectImmediately;
            }
            set
            {
                Settings.ReconnectImmediately = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReconnectImmediately"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReconnectWithInterval"));
            }
        }

        public bool ReconnectWithInterval
        {
            get
            {
                return !Settings.ReconnectImmediately;
            }
            set
            {
                ReconnectImmediately = !value;
            }
        }

        public int ReconnectIntervalSeconds
        {
            get
            {
                return Settings.ReconnectIntervalSeconds;
            }
            set
            {
                Settings.ReconnectIntervalSeconds = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ReconnectIntervalSeconds"));
            }
        }

        public bool ClearContentsWhenReconnecting
        {
            get
            {
                return Settings.ClearContentsWhenReconnecting;
            }
            set
            {
                Settings.ClearContentsWhenReconnecting = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ClearContentsWhenReconnecting"));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ControllerImpl(DeviceTerminalSettings settings, bool showSaveCheckbox)
        {
            Settings = settings.Clone();
            // Seed the list from the profile: the dialog used to always start at "Text only" and never wrote the
            // choice back, so the mode looked like it did nothing at all.
            _EffectiveDisplayMode = ToEffectiveDisplayMode(Settings);
            SaveCheckboxVisibility = ((!showSaveCheckbox) ? Visibility.Collapsed : Visibility.Visible);
            if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Serial originalSerial)
            {
                // The port settings are edited in place through bindings: on a copy, so Cancel leaves the profile as it was
                // (DeviceTerminalSettings.Clone shares the connection object).
                DeviceTerminalSettings.ConnectionSettings.Serial serial = originalSerial.Clone();
                Settings.Connection = serial;
                SerialSettingsHeader = serial.ComPortName + " settings";
                SerialSettingsVisibility = Visibility.Visible;
                TCPSettingsVisibility = Visibility.Collapsed;
                if (serial.AdvancedSettings != null && serial.AdvancedSettings.StopBits == StopBits.None)
                {
                    // "No stop bits" is not a real setting (the port uses one); show what is actually used.
                    serial.AdvancedSettings.StopBits = StopBits.One;
                }
                UpdateMaximumSpeed();
            }
            else if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
            {
                if (tcp is DeviceTerminalSettings.ConnectionSettings.Telnet telnet)
                {
                    TelnetTerminalType = telnet.TerminalType;
                    TelnetLocalEcho = telnet.LocalEcho;
                    TelnetActiveNegotiation = telnet.ActiveNegotiation;
                    TelnetLineMode = telnet.LineMode;
                    TelnetTerminalSpeed = telnet.TerminalSpeed;
                    TelnetXDisplayLocation = telnet.XDisplayLocation;
                    TelnetEnvironmentVariables = telnet.EnvironmentVariables;
                    TelnetTls = telnet.Tls;
                    TelnetTlsAllowInvalidCertificate = telnet.TlsAllowInvalidCertificate;
                    TelnetKeepAliveSeconds = telnet.KeepAliveSeconds;
                    TelnetAutoLoginUserName = telnet.AutoLoginUserName;
                    TelnetLoginPrompts = telnet.LoginPrompts;
                    TelnetPasswordPrompts = telnet.PasswordPrompts;
                    if (telnet.EncryptedSavedPassword != null)
                    {
                        try
                        {
                            TelnetPassword = VisualTTY.PasswordProtector.Decrypt(telnet.EncryptedSavedPassword, "com.sysprogs.smartty.passwords");
                            _EncryptedFor = TelnetPassword;
                            _EncryptedPassword = telnet.EncryptedSavedPassword;
                        }
                        catch (Exception)
                        {
                            // Saved by another Windows user or on another computer: it has to be entered again.
                        }
                    }
                }
                else
                {
                    TcpKeepAliveSeconds = tcp.KeepAliveSeconds;
                }
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
            MaximumSpeed = SerialPortRules.FormatSpeed(SerialPortRules.BytesPerSecond(advancedSerialPortSettings.BaudRate, advancedSerialPortSettings.DataBits,
                advancedSerialPortSettings.Parity != Parity.None, SerialPortRules.StopBitsKindFrom((int)advancedSerialPortSettings.StopBits)));
        }

        public void OnSettingsChanged()
        {
        }

        /// <summary>
        /// Null when the port settings can be used, otherwise why not. The baud rate is checked as typed: text the
        /// binding could not convert never reaches the settings, which would silently keep the previous rate.
        /// </summary>
        public string ValidateSerialSettings(string baudRateText)
        {
            DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings advancedSerialPortSettings = SerialSettings?.AdvancedSettings;
            if (advancedSerialPortSettings == null)
            {
                return null;
            }
            if (!SerialPortRules.TryParseBaudRate(baudRateText, out _, out string error))
            {
                return error;
            }
            return SerialPortRules.ValidateFrame(advancedSerialPortSettings.DataBits, SerialPortRules.StopBitsKindFrom((int)advancedSerialPortSettings.StopBits));
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
        if (_Controller.IsTelnet)
        {
            Title = "Telnet Terminal Settings";
            txtTelnetPassword.Password = _Controller.TelnetPassword ?? "";
            txtTelnetPassword.PasswordChanged += (sender, args) => _Controller.TelnetPassword = txtTelnetPassword.Password;
        }
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
        string serialProblem = _Controller.ValidateSerialSettings(cbBaudRate.Text);
        if (serialProblem != null)
        {
            MessageBoxes.Error("The serial port settings cannot be used: " + serialProblem);
            cbBaudRate.Focus();
            return;
        }
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
