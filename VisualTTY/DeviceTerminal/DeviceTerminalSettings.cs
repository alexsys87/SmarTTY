using System;
using System.ComponentModel;
using System.Drawing;
using System.IO.Ports;
using System.Xml.Serialization;

namespace VisualTTY.DeviceTerminal;

public class DeviceTerminalSettings : INotifyPropertyChanged
{
    [XmlInclude(typeof(Serial))]
    [XmlInclude(typeof(Tcp))]
    [XmlInclude(typeof(Telnet))]
    public abstract class ConnectionSettings
    {
        [XmlType("com.sysprogs.terminal.connection.serial")]
        public class Serial : ConnectionSettings
        {
            public class AdvancedSerialPortSettings
            {
                public int BaudRate { get; set; } = 115200;

                public int DataBits { get; set; } = 8;

                public Parity Parity { get; set; }

                public StopBits StopBits { get; set; } = StopBits.One;

                public Handshake FlowControl { get; set; }

                /// <summary>
                /// Whether DTR is raised when the port is opened. Many boards (Arduino, ESP) reset when DTR changes, so a
                /// connection that must not restart the device switches it off. The driver may still raise DTR for a
                /// moment while it opens the port.
                /// </summary>
                public bool DtrOnOpen { get; set; } = true;

                /// <summary>Whether RTS is raised when the port is opened (ignored with RTS/CTS flow control).</summary>
                public bool RtsOnOpen { get; set; } = true;

                public AdvancedSerialPortSettings Clone()
                {
                    return (AdvancedSerialPortSettings)MemberwiseClone();
                }
            }

            public string ComPortName { get; set; }

            public string DeviceDescription { get; set; }

            public AdvancedSerialPortSettings AdvancedSettings { get; set; } = new AdvancedSerialPortSettings();

            [XmlIgnore]
            public override string FileNameBase => DeviceDescription + "_" + ComPortName;

            public override string ToString()
            {
                if (!string.IsNullOrEmpty(DeviceDescription))
                {
                    return $"{DeviceDescription} ({ComPortName})";
                }
                return ComPortName;
            }

            public override bool IsValid()
            {
                return !string.IsNullOrEmpty(ComPortName);
            }

            public Serial Clone()
            {
                Serial serial = (Serial)MemberwiseClone();
                serial.AdvancedSettings = AdvancedSettings?.Clone();
                return serial;
            }
        }

        [XmlType("com.sysprogs.terminal.connection.tcp")]
        public class Tcp : ConnectionSettings
        {
            public string Host { get; set; }

            public int Port { get; set; }

            /// <summary>
            /// How often an idle connection is kept alive: a raw TCP connection enables the operating system's TCP
            /// keep-alive, a telnet connection additionally sends a harmless NOP command. 0 switches the keep-alive off.
            /// </summary>
            public int KeepAliveSeconds { get; set; }

            [XmlIgnore]
            public override string FileNameBase => Host + "_" + Port;

            public override string ToString()
            {
                return Host + ":" + Port;
            }

            public override bool IsValid()
            {
                if (!string.IsNullOrEmpty(Host))
                {
                    return Port > 0;
                }
                return false;
            }

            /// <summary>Copy of the endpoint that keeps its kind (a telnet endpoint stays a telnet endpoint).</summary>
            public Tcp Clone()
            {
                return (Tcp)MemberwiseClone();
            }
        }

        /// <summary>
        /// A telnet endpoint: a TCP connection that speaks the Telnet protocol (the server's option negotiation is
        /// answered and the commands are not shown), as opposed to <see cref="Tcp"/>, which shows every byte as it is.
        /// Telnet derives from Tcp, so everything that only needs host and port treats both alike; code that must
        /// tell them apart checks for Telnet first.
        /// </summary>
        [XmlType("com.sysprogs.terminal.connection.telnet")]
        public class Telnet : Tcp
        {
            /// <summary>When typed characters are shown by the terminal itself instead of waiting for the server's echo.</summary>
            public enum LocalEchoMode
            {
                /// <summary>Until the server announces that it echoes (option ECHO), and not any more after that.</summary>
                Auto,
                Always,
                Never
            }

            public const int DefaultPort = 23;

            public const string DefaultTerminalType = "xterm";

            /// <summary>The port of telnet over TLS (telnets).</summary>
            public const int DefaultTlsPort = 992;

            /// <summary>The terminal speed (transmit,receive) that new connections report when the server asks.</summary>
            public const string DefaultTerminalSpeed = "38400,38400";

            /// <summary>Reported to the server when it asks (option TERMINAL-TYPE).</summary>
            public string TerminalType { get; set; } = DefaultTerminalType;

            public LocalEchoMode LocalEcho { get; set; } = LocalEchoMode.Auto;

            /// <summary>
            /// Offer the terminal type, the window size and suppress-go-ahead at connect, as most telnet clients do,
            /// instead of waiting until the server asks for them.
            /// </summary>
            public bool ActiveNegotiation { get; set; }

            /// <summary>
            /// Agree to LINEMODE (RFC 1184) when the server asks for it: the line is then edited here and sent when it
            /// ends. Off by default: the server is refused and keeps sending characters one by one, which every
            /// server supports.
            /// </summary>
            public bool LineMode { get; set; }

            /// <summary>
            /// Reported to the server when it asks (option TSPEED): "9600" or "38400,9600" (transmit,receive).
            /// Empty: the option is refused, as before it was supported.
            /// </summary>
            public string TerminalSpeed { get; set; }

            /// <summary>
            /// Reported to the server when it asks (option XDISPLOC): host:display[.screen], for example "pc:0.0".
            /// Empty: the option is refused. The server's X programs connect to this display over the network.
            /// </summary>
            public string XDisplayLocation { get; set; }

            /// <summary>
            /// The environment variables handed to the server when it asks (option NEW-ENVIRON), one NAME=value per
            /// line. Empty: the option is refused. Some servers act upon variables such as USER.
            /// </summary>
            public string EnvironmentVariables { get; set; }

            /// <summary>
            /// Telnet over TLS (telnets): the TLS handshake is made right after connecting, before any Telnet data.
            /// The port is <see cref="DefaultTlsPort"/> when none is given.
            /// </summary>
            public bool Tls { get; set; }

            /// <summary>
            /// Accept any certificate of the server (self-signed, expired, for another name). The connection is still
            /// encrypted but not protected against someone who sits in the middle. Only meaningful with TLS
            /// (<see cref="Tls"/> or <see cref="StartTls"/>).
            /// </summary>
            public bool TlsAllowInvalidCertificate { get; set; }

            /// <summary>TLS negotiated inside a plain telnet session (Telnet option START_TLS, 46).</summary>
            public enum StartTlsMode
            {
                /// <summary>The option is refused (and with <see cref="Tls"/> it is not needed).</summary>
                Off,

                /// <summary>Agreed to when the server asks for it; a server that does not ask is used without TLS.</summary>
                WhenOffered,

                /// <summary>
                /// Offered at once, and nothing typed is sent before the handshake; the connection ends when the server
                /// refuses or sends text first, so a password never travels in clear text.
                /// </summary>
                Required
            }

            /// <summary>STARTTLS on a plain connection; ignored with <see cref="Tls"/>, which is TLS from the first byte.</summary>
            public StartTlsMode StartTls { get; set; }

            /// <summary>
            /// The certificate presented when the TLS server asks for one: the thumbprint of a certificate in the personal
            /// store of Windows (with its private key), or the path of a .pfx / .p12 file without a password. Empty: none.
            /// </summary>
            public string TlsClientCertificate { get; set; }

            /// <summary>
            /// Hand the server sensible values that were not configured: the terminal speed
            /// <see cref="DefaultTerminalSpeed"/>, the X display of this computer (its DISPLAY variable, when set) and
            /// the user name of the automatic login as USER. NEW-ENVIRON is then also agreed to when there is nothing to
            /// send (the server learns that the variables it asks for are not defined). Off: only what is configured is
            /// handed over, and an option with nothing configured is refused.
            /// </summary>
            public bool SendAutomaticValues { get; set; } = true;

            /// <summary>True when the session is encrypted or may become encrypted.</summary>
            [XmlIgnore]
            public bool UsesTls => Tls || StartTls != StartTlsMode.Off;

            /// <summary>What is reported for TSPEED: the configured speed, else the default one with automatic values.</summary>
            public string EffectiveTerminalSpeed()
            {
                if (!string.IsNullOrWhiteSpace(TerminalSpeed))
                {
                    return TerminalSpeed.Trim();
                }
                return SendAutomaticValues ? DefaultTerminalSpeed : null;
            }

            /// <summary>
            /// What is reported for XDISPLOC: the configured location, else (with automatic values) the X display of this
            /// computer, <paramref name="localDisplay"/> (its DISPLAY variable), when that names a host. A display
            /// without a host (":0") is of no use to a program on the server.
            /// </summary>
            public string EffectiveXDisplayLocation(string localDisplay)
            {
                if (!string.IsNullOrWhiteSpace(XDisplayLocation))
                {
                    return XDisplayLocation.Trim();
                }
                if (!SendAutomaticValues || string.IsNullOrWhiteSpace(localDisplay))
                {
                    return null;
                }
                localDisplay = localDisplay.Trim();
                int colon = localDisplay.LastIndexOf(':');
                return colon > 0 ? localDisplay : null;
            }

            /// <summary>
            /// The NEW-ENVIRON variables (one NAME=value per line): with automatic values the user name of the automatic
            /// login comes first as USER, so a USER line of the user replaces it. A name that could be taken for an option
            /// of the server's login program (it starts with '-') or that contains blanks is never sent automatically.
            /// </summary>
            public string EffectiveEnvironmentVariables()
            {
                string configured = EnvironmentVariables ?? "";
                if (!SendAutomaticValues || !IsSafeUserName(AutoLoginUserName))
                {
                    return configured;
                }
                return "USER=" + AutoLoginUserName.Trim() + "\n" + configured;
            }

            private static bool IsSafeUserName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }
                name = name.Trim();
                if (name.Length > 64 || name[0] == '-')
                {
                    return false;
                }
                foreach (char c in name)
                {
                    if (c <= 0x20 || c == 0x7F || c == '=')
                    {
                        return false;
                    }
                }
                return true;
            }

            // --- automatic login: typed when the server asks, see TelnetAutoLogin ---

            /// <summary>Empty: the user name is not typed (a device that asks only for a password).</summary>
            public string AutoLoginUserName { get; set; }

            /// <summary>
            /// The password, encrypted for the current Windows user the same way as the saved SSH passwords (hence the
            /// name: exporting settings without passwords leaves exactly this element out). Null: no password.
            /// </summary>
            public byte[] EncryptedSavedPassword;

            /// <summary>Texts a login prompt ends with, separated by ';'. Empty: login:, username:, user name:, user:.</summary>
            public string LoginPrompts { get; set; }

            /// <summary>Texts a password prompt ends with, separated by ';'. Empty: password:, passcode:.</summary>
            public string PasswordPrompts { get; set; }

            /// <summary>True when there is something to type at a prompt.</summary>
            [XmlIgnore]
            public bool HasAutoLogin => !string.IsNullOrEmpty(AutoLoginUserName) || EncryptedSavedPassword != null;

            /// <summary>
            /// Saved telnet and raw TCP connections to the same host and port are different connections,
            /// so they must not share a file.
            /// </summary>
            [XmlIgnore]
            public override string FileNameBase => "telnet_" + Host + "_" + Port;

            /// <summary>
            /// Reads what a user types for a quick connection: "host", "host:port", "[ipv6]:port", a bare IPv6 address,
            /// each with an optional "telnet://" in front, or "telnets://" for telnet over TLS. The port is
            /// <see cref="DefaultPort"/> (<see cref="DefaultTlsPort"/> for telnets) when none is given.
            /// </summary>
            public static Telnet Parse(string text)
            {
                const string expected = "Expected: hostname or hostname:port";
                text = (text ?? "").Trim();
                bool tls = false;
                if (text.StartsWith("telnets://", System.StringComparison.OrdinalIgnoreCase))
                {
                    tls = true;
                    text = text.Substring("telnets://".Length).TrimEnd('/');
                }
                else if (text.StartsWith("telnet://", System.StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring("telnet://".Length).TrimEnd('/');
                }
                string host = text;
                int port = tls ? DefaultTlsPort : DefaultPort;
                if (text.StartsWith("[", System.StringComparison.Ordinal))
                {
                    int closing = text.IndexOf(']');
                    if (closing == -1)
                    {
                        throw new System.FormatException(expected);
                    }
                    host = text.Substring(1, closing - 1);
                    string rest = text.Substring(closing + 1);
                    if (rest.StartsWith(":", System.StringComparison.Ordinal))
                    {
                        port = ParsePort(rest.Substring(1));
                    }
                    else if (rest.Length != 0)
                    {
                        throw new System.FormatException(expected);
                    }
                }
                else
                {
                    int colon = text.IndexOf(':');
                    // A single colon separates the port; several colons are an IPv6 address without a port.
                    if (colon != -1 && colon == text.LastIndexOf(':'))
                    {
                        host = text.Substring(0, colon);
                        port = ParsePort(text.Substring(colon + 1));
                    }
                }
                if (host.Length == 0)
                {
                    throw new System.FormatException(expected);
                }
                return new Telnet
                {
                    Host = host,
                    Port = port,
                    Tls = tls,
                    TerminalSpeed = DefaultTerminalSpeed
                };
            }

            private static int ParsePort(string text)
            {
                if (!int.TryParse(text.Trim(), out int port) || port < 1 || port > 65535)
                {
                    throw new System.FormatException("Invalid port number: '" + text + "' (expected 1-65535)");
                }
                return port;
            }
        }

        [XmlIgnore]
        public abstract string FileNameBase { get; }

        public abstract bool IsValid();
    }

    [XmlInclude(typeof(Color))]
    public struct ColorWrapper
    {
        public int Alpha;

        public int Red;

        public int Green;

        public int Blue;

        public static implicit operator Color(ColorWrapper wrapper)
        {
            return Color.FromArgb(wrapper.Alpha, wrapper.Red, wrapper.Green, wrapper.Blue);
        }

        public static implicit operator ColorWrapper(Color color)
        {
            return new ColorWrapper
            {
                Alpha = color.A,
                Red = color.R,
                Green = color.G,
                Blue = color.B
            };
        }
    }

    public struct TerminalColors
    {
        public ColorWrapper Background { get; set; }

        public ColorWrapper Disconnected { get; set; }

        public ColorWrapper Text { get; set; }

        public ColorWrapper Echo { get; set; }

        public ColorWrapper Inactive { get; set; }
    }

    public enum CharacterDisplayMode
    {
        ASCII,
        HEX,
        Both
    }

    public static class CharacterDisplayModeHelper
    {
        /// <summary>
        /// Resolves a persisted mode into the mode that is actually displayed. Older settings files could enable
        /// the text pane next to the HEX dump without having a separate "Both" mode; such files are shown as the
        /// combined view they were displaying, while a new "HEX only" profile explicitly stores ShowTextView=false.
        /// </summary>
        public static CharacterDisplayMode Resolve(CharacterDisplayMode mode, HexTerminalSettings hexSettings)
        {
            if (mode == CharacterDisplayMode.HEX && (hexSettings == null || hexSettings.ShowTextView))
            {
                return CharacterDisplayMode.Both;
            }
            return mode;
        }
    }

    public enum LineEndingType
    {
        CR,
        LF,
        CRLF
    }

    public class HexTerminalSettings
    {
        public int MaximumBytesPerLine { get; set; }

        public bool ShowTextView { get; set; }

        public bool BreaksAroundEcho { get; set; }

        public bool AutoSend { get; set; }

        public bool SendAsHex { get; set; }

        public int TimeoutForAutoBreak { get; set; }

        public HexTerminalSettings Clone()
        {
            return (HexTerminalSettings)MemberwiseClone();
        }
    }

    public ConnectionSettings Connection;

    public long LastConnectionTime;

    private bool _EchoTypedCharacters;

    private bool _ClearContentsWhenReconnecting;

    private bool _ReconnectAutomatically;

    // On by default; an explicit backing field, so a Clone() and a settings file without the element agree.
    private bool _SyncRemoteTerminalSize = true;

    // Automatic reconnect behavior (see ReconnectAutomatically/ReconnectImmediately/ReconnectIntervalSeconds).
    // Explicit backing fields, not auto-property initializers, so a Clone() keeps the same defaults.
    private bool _ReconnectImmediately;

    private int _ReconnectIntervalSeconds = 2;

    public string BackspaceSequence;

    private TerminalColors _Colors = new TerminalColors
    {
        Background = Color.Black,
        Text = Color.LightGray,
        Disconnected = Color.DarkGray,
        Echo = Color.LightGreen,
        Inactive = Color.DarkGray
    };

    public bool EchoTypedCharacters
    {
        get
        {
            return _EchoTypedCharacters;
        }
        set
        {
            _EchoTypedCharacters = value;
            OnPropertyChanged("EchoTypedCharacters");
        }
    }

    public bool ClearContentsWhenReconnecting
    {
        get
        {
            return _ClearContentsWhenReconnecting;
        }
        set
        {
            _ClearContentsWhenReconnecting = value;
            OnPropertyChanged("ClearContentsWhenReconnecting");
        }
    }

    /// <summary>
    /// Tell a Linux shell on the device the size of the terminal window (with a command that sets COLUMNS, LINES
    /// and the tty size in front of the next command) so that mc, htop and the like fill the window. On by default; it acts only after the device
    /// has shown the prompt of a Linux shell ("# " or "$ "), never at a bootloader or a "login:" prompt, because
    /// the text is typed into the device. Telnet connections do not use it (they pass the size on themselves).
    /// </summary>
    public bool SyncRemoteTerminalSize
    {
        get
        {
            return _SyncRemoteTerminalSize;
        }
        set
        {
            _SyncRemoteTerminalSize = value;
            OnPropertyChanged("SyncRemoteTerminalSize");
        }
    }

    public bool ReconnectAutomatically
    {
        get
        {
            return _ReconnectAutomatically;
        }
        set
        {
            _ReconnectAutomatically = value;
            OnPropertyChanged("ReconnectAutomatically");
        }
    }

    /// <summary>
    /// When <see cref="ReconnectAutomatically"/> is set, reconnect right away after the connection breaks
    /// instead of waiting <see cref="ReconnectIntervalSeconds"/> seconds. Bound to the "immediately" radio
    /// button in the Connect window.
    /// </summary>
    public bool ReconnectImmediately
    {
        get
        {
            return _ReconnectImmediately;
        }
        set
        {
            _ReconnectImmediately = value;
            OnPropertyChanged("ReconnectImmediately");
            OnPropertyChanged("ReconnectWithInterval");
        }
    }

    /// <summary>The inverse of <see cref="ReconnectImmediately"/> (bound to the "repeat" radio button).</summary>
    [XmlIgnore]
    public bool ReconnectWithInterval
    {
        get
        {
            return !_ReconnectImmediately;
        }
        set
        {
            ReconnectImmediately = !value;
        }
    }

    /// <summary>
    /// How long to wait between automatic reconnect attempts, in seconds (1-5, default 2). Only used when
    /// <see cref="ReconnectAutomatically"/> is set and <see cref="ReconnectImmediately"/> is off.
    /// </summary>
    public int ReconnectIntervalSeconds
    {
        get
        {
            return _ReconnectIntervalSeconds;
        }
        set
        {
            _ReconnectIntervalSeconds = Math.Max(1, Math.Min(value, 5));
            OnPropertyChanged("ReconnectIntervalSeconds");
        }
    }

    public CharacterDisplayMode DisplayMode { get; set; }

    public TerminalColors Colors
    {
        get
        {
            return _Colors;
        }
        set
        {
            if (value.Inactive.Alpha == 0)
            {
                value.Inactive = _Colors.Inactive;
            }
            _Colors = value;
        }
    }

    public HexTerminalSettings HexSettings { get; set; } = new HexTerminalSettings
    {
        MaximumBytesPerLine = 16,
        ShowTextView = true,
        BreaksAroundEcho = true,
        AutoSend = true,
        SendAsHex = true,
        TimeoutForAutoBreak = 0
    };

    public LineEndingType LineEnding { get; set; } = LineEndingType.LF;

    public bool TreatLFAsCRLF { get; set; }

    /// <summary>
    /// Encoding of the text exchanged with the device (see <see cref="TerminalEncodings"/>). It is used to decode
    /// received data and to encode typed and pasted text. Settings saved before this option existed have no
    /// value in the file and get the UTF-8 default.
    /// </summary>
    public string TextEncoding { get; set; } = TerminalEncodings.DefaultName;

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public DeviceTerminalSettings Clone()
    {
        DeviceTerminalSettings deviceTerminalSettings = (DeviceTerminalSettings)MemberwiseClone();
        deviceTerminalSettings.HexSettings = deviceTerminalSettings.HexSettings?.Clone();
        return deviceTerminalSettings;
    }
}
