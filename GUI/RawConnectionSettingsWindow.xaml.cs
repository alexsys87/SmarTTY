using SmarTTY.Controls;
using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        public bool IsNetconsole => Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Netconsole;

        // --- netconsole receiver: kept here while the window is open and turned into the connection by
        // EffectiveConnectionSettings (the ports are text, so a wrong value can be reported instead of lost) ---

        public class NetconsoleInputChoice
        {
            public DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode Value { get; set; }

            public string Text { get; set; }
        }

        public System.Collections.Generic.IReadOnlyList<NetconsoleInputChoice> NetconsoleInputChoices { get; } = new NetconsoleInputChoice[]
        {
            new NetconsoleInputChoice { Value = DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Line, Text = "Line by line: edited below the terminal, sent on Enter" },
            new NetconsoleInputChoice { Value = DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode.Character, Text = "Byte by byte: every key is sent at once" }
        };

        public class NetconsoleFormatChoice
        {
            public DeviceTerminalSettings.ConnectionSettings.Netconsole.MessageFormat Value { get; set; }

            public string Text { get; set; }
        }

        public System.Collections.Generic.IReadOnlyList<NetconsoleFormatChoice> NetconsoleFormatChoices { get; } = new NetconsoleFormatChoice[]
        {
            new NetconsoleFormatChoice { Value = DeviceTerminalSettings.ConnectionSettings.Netconsole.MessageFormat.Auto, Text = "Decode extended messages (timestamp and level, like dmesg)" },
            new NetconsoleFormatChoice { Value = DeviceTerminalSettings.ConnectionSettings.Netconsole.MessageFormat.Raw, Text = "Show every datagram as it is" }
        };

        public string NetconsoleLocalAddress { get; set; }

        private string _NetconsoleLocalPort = DeviceTerminalSettings.ConnectionSettings.Netconsole.DefaultPort.ToString();

        public string NetconsoleLocalPort
        {
            get
            {
                return _NetconsoleLocalPort;
            }
            set
            {
                _NetconsoleLocalPort = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NetconsoleHint"));
            }
        }

        public string NetconsoleRemoteHost { get; set; }

        public string NetconsoleRemotePort { get; set; } = DeviceTerminalSettings.ConnectionSettings.Netconsole.DefaultPort.ToString();

        public bool NetconsoleAcceptOnlyFromRemoteHost { get; set; }

        public DeviceTerminalSettings.ConnectionSettings.Netconsole.InputMode NetconsoleInput { get; set; }

        public DeviceTerminalSettings.ConnectionSettings.Netconsole.MessageFormat NetconsoleFormat { get; set; }

        public bool NetconsoleColorizeLevels { get; set; } = true;

        public bool NetconsoleShowSender { get; set; }

        public bool NetconsoleLocalEcho { get; set; } = true;

        public Visibility NetconsoleSettingsVisibility { get; } = Visibility.Collapsed;

        // The prompt and the echo of the terminal do not apply to netconsole (it has its own echo setting).
        public Visibility PromptSettingsVisibility => IsNetconsole ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>What to set on the device so that it sends here: the netconsole parameter and the U-Boot variables.</summary>
        public string NetconsoleHint
        {
            get
            {
                string address = LocalAddressForHint();
                string port = ParsePort(NetconsoleLocalPort ?? "") is int value && value > 0 ? value.ToString() : DeviceTerminalSettings.ConnectionSettings.Netconsole.DefaultPort.ToString();
                return "Linux: netconsole=+@/eth0," + port + "@" + address + "/  (\"+\" for extended messages; on the kernel command line or with modprobe netconsole)\r\n"
                    + "U-Boot: setenv ncip " + address + (port != "6666" ? ":" + port : "") + "; setenv stdin nc; setenv stdout nc; setenv stderr nc";
            }
        }

        /// <summary>The address to give the device: the one typed in, or an IPv4 address of this computer.</summary>
        private string LocalAddressForHint()
        {
            if (!string.IsNullOrWhiteSpace(NetconsoleLocalAddress))
            {
                return NetconsoleLocalAddress.Trim();
            }
            try
            {
                foreach (System.Net.IPAddress address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address))
                    {
                        return address.ToString();
                    }
                }
            }
            catch
            {
            }
            return "<this computer>";
        }

        /// <summary>Null when the netconsole settings can be used, otherwise why not.</summary>
        public string ValidateNetconsole()
        {
            if (!IsNetconsole)
            {
                return null;
            }
            if (ParsePort(NetconsoleLocalPort ?? "") == 0)
            {
                return "Enter the UDP port to listen on (1-65535; netconsole sends to 6666 unless told otherwise).";
            }
            if (ParsePort(NetconsoleRemotePort ?? "") == 0)
            {
                return "Enter the UDP port the device reads input on (1-65535; U-Boot uses 6666).";
            }
            if (!string.IsNullOrWhiteSpace(NetconsoleLocalAddress) && NetconsoleLocalAddress.Trim().Contains(" "))
            {
                return "The local address must be an IP address of this computer (or empty for every interface).";
            }
            if (NetconsoleAcceptOnlyFromRemoteHost && string.IsNullOrWhiteSpace(NetconsoleRemoteHost))
            {
                return "Enter the device address to show only its messages.";
            }
            return null;
        }

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

        private bool _TelnetTls;

        public bool TelnetTls
        {
            get
            {
                return _TelnetTls;
            }
            set
            {
                _TelnetTls = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TelnetTls"));
                OnTelnetTlsChanged();
            }
        }

        public bool TelnetTlsAllowInvalidCertificate { get; set; }

        public class TelnetStartTlsChoice
        {
            public DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode Value { get; set; }

            public string Text { get; set; }
        }

        public System.Collections.Generic.IReadOnlyList<TelnetStartTlsChoice> TelnetStartTlsChoices { get; } = new TelnetStartTlsChoice[]
        {
            new TelnetStartTlsChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off, Text = "Off (refused)" },
            new TelnetStartTlsChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.WhenOffered, Text = "When the server offers it" },
            new TelnetStartTlsChoice { Value = DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Required, Text = "Required (no clear-text session)" }
        };

        private DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode _TelnetStartTls;

        public DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode TelnetStartTls
        {
            get
            {
                return _TelnetStartTls;
            }
            set
            {
                _TelnetStartTls = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TelnetStartTls"));
                OnTelnetTlsChanged();
            }
        }

        // STARTTLS is the way to TLS on a plain connection; with telnets the session is encrypted from the start.
        public bool TelnetStartTlsEnabled => !TelnetTls;

        // The certificate options matter only when TLS is used one way or the other.
        public bool TelnetTlsInUse => TelnetTls || TelnetStartTls != DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off;

        private void OnTelnetTlsChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TelnetStartTlsEnabled"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TelnetTlsInUse"));
        }

        private string _TelnetTlsClientCertificate;

        public string TelnetTlsClientCertificate
        {
            get
            {
                return _TelnetTlsClientCertificate;
            }
            set
            {
                _TelnetTlsClientCertificate = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TelnetTlsClientCertificate"));
            }
        }

        public bool TelnetSendAutomaticValues { get; set; } = true;

        private int _TelnetKeepAliveSeconds = DeviceTerminalSettings.ConnectionSettings.Tcp.DefaultKeepAliveSeconds;

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

        private int _TcpKeepAliveSeconds = DeviceTerminalSettings.ConnectionSettings.Tcp.DefaultKeepAliveSeconds;

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

        // The saved password that could not be decrypted here (saved by another Windows user or on another computer). It stays
        // as it was until a password is entered, so that opening and closing the window does not erase it for the place where
        // it still works.
        private byte[] _UnreadableEncryptedPassword;

        // The password of a quick connection (user:password@host): it lives in memory only and is never saved, but it is
        // shown in the password box and kept for as long as the box is left alone.
        private string _QuickConnectPassword;

        // Why the password that was typed in could not be saved (too long, no key container), or null.
        private string _PasswordNotSavedReason;

        /// <summary>Called when the user changes the password: what could not be read before is then replaced on purpose.</summary>
        public void NotePasswordEdited()
        {
            _UnreadableEncryptedPassword = null;
        }

        /// <summary>
        /// Why the password that was typed in cannot be saved, or null when it can (or there is none). The connection is then
        /// saved without a password rather than failing while the window closes, but the user is told.
        /// </summary>
        public string PasswordNotSavedReason
        {
            get
            {
                EncryptTelnetPassword();
                return _PasswordNotSavedReason;
            }
        }

        private byte[] EncryptTelnetPassword()
        {
            if (string.IsNullOrEmpty(TelnetPassword))
            {
                _PasswordNotSavedReason = null;
                // Nothing typed in: a saved password that this computer cannot read is kept, not erased.
                return _UnreadableEncryptedPassword;
            }
            if (_EncryptedFor != TelnetPassword)
            {
                try
                {
                    _EncryptedPassword = VisualTTY.PasswordProtector.Encrypt(TelnetPassword, "com.sysprogs.smartty.passwords");
                    _EncryptedFor = TelnetPassword;
                    _PasswordNotSavedReason = null;
                }
                catch (Exception exception)
                {
                    // Too long or no key container.
                    _EncryptedPassword = null;
                    _EncryptedFor = TelnetPassword;
                    _PasswordNotSavedReason = exception.Message;
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
                if (IsNetconsole)
                {
                    // A port that is not a number is 0, which makes the connection invalid instead of throwing.
                    return new DeviceTerminalSettings.ConnectionSettings.Netconsole
                    {
                        LocalAddress = string.IsNullOrWhiteSpace(NetconsoleLocalAddress) ? null : NetconsoleLocalAddress.Trim().Trim('[', ']'),
                        LocalPort = ParsePort(NetconsoleLocalPort ?? ""),
                        RemoteHost = string.IsNullOrWhiteSpace(NetconsoleRemoteHost) ? null : NetconsoleRemoteHost.Trim().Trim('[', ']'),
                        RemotePort = ParsePort(NetconsoleRemotePort ?? ""),
                        AcceptOnlyFromRemoteHost = NetconsoleAcceptOnlyFromRemoteHost,
                        Input = NetconsoleInput,
                        Format = NetconsoleFormat,
                        ColorizeLevels = NetconsoleColorizeLevels,
                        ShowSender = NetconsoleShowSender,
                        LocalEcho = NetconsoleLocalEcho
                    };
                }
                SplitEndpoint(TCPEndpoint, out string host, out int? givenPort, out bool schemeTls);
                bool tls = TelnetTls || (IsTelnet && schemeTls);
                // A telnet server is expected on the telnet port unless another one is given; a port that is not a
                // number is 0, which makes the connection invalid instead of throwing while the window closes.
                int port = givenPort ?? (IsTelnet ? (tls ? DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTlsPort : DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultPort) : 0);
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
                        Tls = tls,
                        StartTls = tls ? DeviceTerminalSettings.ConnectionSettings.Telnet.StartTlsMode.Off : TelnetStartTls,
                        TlsAllowInvalidCertificate = TelnetTlsInUse && TelnetTlsAllowInvalidCertificate,
                        TlsClientCertificate = TelnetTlsInUse && !string.IsNullOrWhiteSpace(TelnetTlsClientCertificate) ? TelnetTlsClientCertificate.Trim() : null,
                        SendAutomaticValues = TelnetSendAutomaticValues,
                        KeepAliveSeconds = TelnetKeepAliveSeconds,
                        AutoLoginUserName = string.IsNullOrWhiteSpace(TelnetAutoLoginUserName) ? null : TelnetAutoLoginUserName.Trim(),
                        EncryptedSavedPassword = EncryptTelnetPassword(),
                        AutoLoginPassword = !string.IsNullOrEmpty(_QuickConnectPassword) && TelnetPassword == _QuickConnectPassword ? _QuickConnectPassword : null,
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

        /// <summary>
        /// Reads "host", "host:port", "[ipv6]:port" or a bare IPv6 address (several colons, no port), with an optional
        /// telnet:// or telnets:// in front (the latter switches TLS on for a telnet connection). The port is null when
        /// none is given and 0 when it is not a valid number. <paramref name="schemeTls"/> tells whether the text asked
        /// for TLS with telnets://; nothing is changed here, because this is also called just to validate the text.
        /// </summary>
        private static void SplitEndpoint(string endpoint, out string host, out int? port, out bool schemeTls)
        {
            endpoint = (endpoint ?? "").Trim();
            schemeTls = false;
            if (endpoint.StartsWith("telnets://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring("telnets://".Length).TrimEnd('/');
                schemeTls = true;
            }
            else if (endpoint.StartsWith("telnet://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring("telnet://".Length).TrimEnd('/');
            }
            else if (endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring("tcp://".Length).TrimEnd('/');
            }
            else if (endpoint.StartsWith("raw://", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring("raw://".Length).TrimEnd('/');
            }
            port = null;
            if (endpoint.StartsWith("[", StringComparison.Ordinal) && endpoint.IndexOf(']') > 0)
            {
                int closing = endpoint.IndexOf(']');
                host = endpoint.Substring(1, closing - 1);
                string rest = endpoint.Substring(closing + 1);
                if (rest.StartsWith(":", StringComparison.Ordinal))
                {
                    port = ParsePort(rest.Substring(1));
                }
                else if (rest.Length != 0)
                {
                    // "[::1]23" or "[::1]x": not a port, so the endpoint is rejected instead of the text being ignored.
                    port = 0;
                }
                return;
            }
            int colon = endpoint.IndexOf(':');
            if (colon != -1 && colon == endpoint.LastIndexOf(':'))
            {
                host = endpoint.Substring(0, colon);
                port = ParsePort(endpoint.Substring(colon + 1));
                return;
            }
            host = endpoint;
        }

        private static int ParsePort(string text)
        {
            return int.TryParse(text.Trim(), out int port) && port > 0 && port <= 65535 ? port : 0;
        }

        /// <summary>
        /// Null when the Telnet options can be used as written. A speed, an X display location or an environment line
        /// that is not valid is left out of the negotiation, so the person is told instead of wondering why the server
        /// did not get it.
        /// </summary>
        public string ValidateTelnetOptions()
        {
            if (!IsTelnet)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(TelnetTerminalSpeed) && SmarTTY.Transport.TelnetStream.NormalizeTerminalSpeed(TelnetTerminalSpeed) == null)
            {
                return "The terminal speed must be one number or two separated by a comma (each of up to 9 digits), for example 38400 or 38400,38400.";
            }
            if (!string.IsNullOrWhiteSpace(TelnetXDisplayLocation) && SmarTTY.Transport.TelnetStream.NormalizeDisplayLocation(TelnetXDisplayLocation) == null)
            {
                return "The X display location must be printable ASCII text of at most 255 characters, for example 192.168.0.5:0.";
            }
            System.Collections.Generic.List<string> invalid = SmarTTY.Transport.TelnetEnvironment.InvalidLines(TelnetEnvironmentVariables);
            if (invalid.Count > 0)
            {
                string first = invalid[0].Length > 60 ? invalid[0].Substring(0, 57) + "..." : invalid[0];
                return invalid.Count + " environment variable line" + (invalid.Count == 1 ? " is" : "s are") + " not valid and would not be sent (one NAME=value per line, at most "
                    + SmarTTY.Transport.TelnetEnvironment.MaxVariables + " variables): " + first;
            }
            return null;
        }

        /// <summary>Null when the endpoint can be used, otherwise why not.</summary>
        public string ValidateEndpoint()
        {
            if (IsNetconsole)
            {
                return ValidateNetconsole();
            }
            if (!(Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp))
            {
                return null;
            }
            SplitEndpoint(TCPEndpoint, out string host, out int? port, out _);
            if (host != null && host.StartsWith("[", StringComparison.Ordinal))
            {
                return "The IPv6 address is missing its closing ']' (write it as [address]:port).";
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                return "Enter the host name or IP address to connect to.";
            }
            if (port == 0 || (port == null && !IsTelnet))
            {
                return "Enter a port number from 1 to 65535 after the host name (host:port).";
            }
            return null;
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
                    TelnetStartTls = telnet.StartTls;
                    TelnetTlsAllowInvalidCertificate = telnet.TlsAllowInvalidCertificate;
                    TelnetTlsClientCertificate = telnet.TlsClientCertificate;
                    TelnetSendAutomaticValues = telnet.SendAutomaticValues;
                    TelnetKeepAliveSeconds = telnet.KeepAliveSeconds;
                    TelnetAutoLoginUserName = telnet.AutoLoginUserName;
                    TelnetLoginPrompts = telnet.LoginPrompts;
                    TelnetPasswordPrompts = telnet.PasswordPrompts;
                    if (telnet.EncryptedSavedPassword == null && !string.IsNullOrEmpty(telnet.AutoLoginPassword))
                    {
                        // Marked as "encrypted for" itself, so it is not turned into a saved password unless it is edited.
                        TelnetPassword = telnet.AutoLoginPassword;
                        _QuickConnectPassword = telnet.AutoLoginPassword;
                        _EncryptedFor = TelnetPassword;
                    }
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
                            // Saved by another Windows user or on another computer: it has to be entered again here. What was
                            // saved stays as it was unless that happens (it still works where it was saved).
                            _UnreadableEncryptedPassword = telnet.EncryptedSavedPassword;
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
                    // An IPv6 address goes in brackets, so it is not cut at one of its colons when read back.
                    TCPEndpoint = (tcp.Host.Contains(":") ? "[" + tcp.Host + "]" : tcp.Host) + ":" + tcp.Port;
                }
            }
            else if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Netconsole netconsole)
            {
                NetconsoleLocalAddress = netconsole.LocalAddress;
                NetconsoleLocalPort = netconsole.LocalPort.ToString();
                NetconsoleRemoteHost = netconsole.RemoteHost;
                NetconsoleRemotePort = netconsole.RemotePort.ToString();
                NetconsoleAcceptOnlyFromRemoteHost = netconsole.AcceptOnlyFromRemoteHost;
                NetconsoleInput = netconsole.Input;
                NetconsoleFormat = netconsole.Format;
                NetconsoleColorizeLevels = netconsole.ColorizeLevels;
                NetconsoleShowSender = netconsole.ShowSender;
                NetconsoleLocalEcho = netconsole.LocalEcho;
                SerialSettingsVisibility = Visibility.Collapsed;
                TCPSettingsVisibility = Visibility.Collapsed;
                NetconsoleSettingsVisibility = Visibility.Visible;
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

        /// <summary>
        /// Takes the settings as they are now (on the UI thread) and tests them on a worker thread, so the window keeps
        /// painting while a host that does not answer is waited for.
        /// </summary>
        public Task<string> TestConnectionAsync()
        {
            ApplyTCPEndpoint();
            DeviceTerminalSettings.ConnectionSettings connection = EffectiveConnectionSettings;
            return Task.Run(() => DeviceTerminalControl.TestConnection(connection));
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
                DeviceTerminalSettings.ColorWrapper? selectedColor = ColorPickerWindow.Pick(WindowOwnership.ActiveWindow(), colorWrapper);
                if (selectedColor != null)
                {
                    colorProp.SetValue(containerObj, selectedColor.Value, new object[0]);
                    objProp.SetValue(dataContext, containerObj, new object[0]);
                    ((BindingExpressionBase)bindingExpression).UpdateTarget();
                    OnSettingsChanged();
                }
            }
        }

        public void ApplyTCPEndpoint()
        {
            if (IsNetconsole)
            {
                Settings.Connection = EffectiveConnectionSettings;
                return;
            }
            if (Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp)
            {
                // The only place where the text may change another setting: the user has finished typing.
                SplitEndpoint(TCPEndpoint, out _, out _, out bool schemeTls);
                if (IsTelnet && schemeTls && !TelnetTls)
                {
                    TelnetTls = true;
                }
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
        if (_Controller.IsNetconsole)
        {
            Title = "Netconsole Receiver Settings";
        }
        if (_Controller.IsTelnet)
        {
            Title = "Telnet Terminal Settings";
            txtTelnetPassword.Password = _Controller.TelnetPassword ?? "";
            txtTelnetPassword.PasswordChanged += (sender, args) =>
            {
                _Controller.TelnetPassword = txtTelnetPassword.Password;
                _Controller.NotePasswordEdited();
            };
        }
    }

    private bool _Closed;

    private bool _Testing;

    protected override void OnClosing(CancelEventArgs e)
    {
        // The result of a test that is still running would have nowhere to go.
        if (_Testing)
        {
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _Closed = true;
        base.OnClosed(e);
        _Controller.ApplyTCPEndpoint();
    }

    /// <summary>Runs the connection test with the window locked and a wait cursor; rethrows the failure.</summary>
    private async Task<string> RunConnectionTestAsync()
    {
        _Testing = true;
        IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            return await _Controller.TestConnectionAsync();
        }
        finally
        {
            _Testing = false;
            IsEnabled = true;
            Cursor = null;
        }
    }

    /// <summary>Puts the keyboard where the address or the port that was rejected is typed.</summary>
    private void FocusEndpointBox()
    {
        if (_Controller.IsNetconsole)
        {
            txtNetconsoleLocalPort.Focus();
            return;
        }
        txtHost.Focus();
    }

    private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _Controller.UpdateMaximumSpeed();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_Testing)
        {
            return;
        }
        string endpointProblem = _Controller.ValidateEndpoint();
        if (endpointProblem != null)
        {
            MessageBoxes.Error(endpointProblem);
            FocusEndpointBox();
            return;
        }
        try
        {
            string text = await RunConnectionTestAsync();
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

    private void SelectClientCertificate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Security.Cryptography.X509Certificates.X509Store store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            try
            {
                store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                System.Security.Cryptography.X509Certificates.X509Certificate2Collection usable = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
                foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate in store.Certificates)
                {
                    // Only a certificate with its private key can prove anything to the server.
                    if (certificate.HasPrivateKey)
                    {
                        usable.Add(certificate);
                    }
                }
                if (usable.Count == 0)
                {
                    MessageBoxes.Info("Your personal certificate store has no certificate with a private key. Import one first (open the .pfx / .p12 file with a double click).");
                    return;
                }
                System.Security.Cryptography.X509Certificates.X509Certificate2Collection chosen = System.Security.Cryptography.X509Certificates.X509Certificate2UI.SelectFromCollection(
                    usable, "Client certificate", "Choose the certificate presented to the telnet server.",
                    System.Security.Cryptography.X509Certificates.X509SelectionFlag.SingleSelection, new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (chosen.Count > 0)
                {
                    _Controller.TelnetTlsClientCertificate = chosen[0].Thumbprint;
                }
            }
            finally
            {
                store.Close();
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void BrowseClientCertificate_Click(object sender, RoutedEventArgs e)
    {
        string certificate = LocalFileDialogWindow.OpenFile(this, "Client certificate (a file without a password)",
            "Certificates with a private key (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*");
        if (certificate != null)
        {
            _Controller.TelnetTlsClientCertificate = certificate;
        }
    }

    private async void OK_Click(object sender, RoutedEventArgs e)
    {
        if (_Testing)
        {
            return;
        }
        string endpointProblem = _Controller.ValidateEndpoint();
        if (endpointProblem != null)
        {
            MessageBoxes.Error(endpointProblem);
            FocusEndpointBox();
            return;
        }
        string telnetProblem = _Controller.ValidateTelnetOptions();
        if (telnetProblem != null)
        {
            MessageBoxes.Error(telnetProblem);
            return;
        }
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
                await RunConnectionTestAsync();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
                return;
            }
            if (!_Closed)
            {
                WarnIfPasswordNotSaved();
                this.DialogResult = true;
            }
            return;
        }
        WarnIfPasswordNotSaved();
        this.DialogResult = true;
    }

    /// <summary>
    /// Says so when the password typed in could not be saved: the connection is then saved without it, which would otherwise show
    /// only at the next connect. Shown when the window is accepted, not on every test, because the settings are built several times.
    /// </summary>
    private void WarnIfPasswordNotSaved()
    {
        string reason = _Controller.PasswordNotSavedReason;
        if (reason != null)
        {
            MessageBoxes.Warning("The password could not be saved (" + reason + "). The connection is saved without it, so the password has to be entered again.");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }
}
