using System.ComponentModel;
using System.Drawing;
using System.IO.Ports;
using System.Xml.Serialization;

namespace VisualTTY.DeviceTerminal;

public class DeviceTerminalSettings : INotifyPropertyChanged
{
    [XmlInclude(typeof(Serial))]
    [XmlInclude(typeof(Tcp))]
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
        HEX
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
