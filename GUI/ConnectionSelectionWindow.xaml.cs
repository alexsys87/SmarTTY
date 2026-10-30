using SmarTTY.Ssh;
using SmarTTY.Forms;
using SmarTTY.GUI.Styles;
using SmarTTY.Native;
using SmarTTY.Transport;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisualTTY;
using VisualTTY.Backend;
using VisualTTY.DeviceTerminal;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Forms.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmarTTY.GUI;

public partial class ConnectionSelectionWindow : Window, IComponentConnector, IStyleConnector
{
    private class MachineConnectionTester : IDisposable
    {
        public enum State
        {
            Checking,
            Succeeded,
            Failed
        }

        // A result is trusted this long: a device that appears later (or goes away) shows up at the next refresh after it.
        private const long ResultLifetimeMs = 30000;

        // A host that does not answer is given up after this long instead of waiting for the operating system.
        private const int CheckTimeoutMs = 5000;

        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        private class Context
        {
            // The result being shown; the previous one stays until a new check has an answer, so the list does not flicker.
            public State State = State.Checking;

            public long CheckedAtMs;

            public bool InProgress;

            public TcpConnector Connector;
        }

        private struct Key
        {
            public string Host;

            public int Port;
        }

        private readonly object _lock = new object();

        private readonly Dictionary<Key, Context> _AllContexts = new Dictionary<Key, Context>();

        private bool _disposed;

        public event Action BackgroundCheckCompleted;

        /// <summary>
        /// The last known state of the port; a check that is due (never made, or older than <see cref="ResultLifetimeMs"/>)
        /// is started in the background. The check opens the TCP connection and closes it at once: it cannot tell whether
        /// a port listens without connecting, so a device that serves one client at a time may be busy for a moment
        /// (the "Probe all connections in the background" option turns this off).
        /// </summary>
        public State CheckMachine(string host, int port)
        {
            Key key = new Key
            {
                Host = host,
                Port = port
            };
            lock (_lock)
            {
                if (_disposed)
                {
                    return State.Failed;
                }
                if (!_AllContexts.TryGetValue(key, out Context context))
                {
                    context = _AllContexts[key] = new Context();
                }
                if (!context.InProgress && (context.CheckedAtMs == 0 || Clock.ElapsedMilliseconds - context.CheckedAtMs >= ResultLifetimeMs))
                {
                    context.InProgress = true;
                    ThreadPool.QueueUserWorkItem(_ => RunCheck(context, host, port));
                }
                return context.State;
            }
        }

        private void RunCheck(Context context, string host, int port)
        {
            TcpConnector connector = new TcpConnector();
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                context.Connector = connector;
            }
            State result;
            try
            {
                connector.Connect(host, port, CheckTimeoutMs).Close();
                result = State.Succeeded;
            }
            catch
            {
                result = State.Failed;
            }
            bool changed;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                changed = context.CheckedAtMs == 0 || context.State != result;
                context.State = result;
                context.CheckedAtMs = Math.Max(1, Clock.ElapsedMilliseconds);
                context.InProgress = false;
                context.Connector = null;
            }
            if (changed)
            {
                BackgroundCheckCompleted?.Invoke();
            }
        }

        public void Dispose()
        {
            List<TcpConnector> running = new List<TcpConnector>();
            lock (_lock)
            {
                _disposed = true;
                foreach (Context context in _AllContexts.Values)
                {
                    if (context.Connector != null)
                    {
                        running.Add(context.Connector);
                    }
                }
            }
            // Ends the checks that are still waiting for an answer; none of them reports back after this.
            foreach (TcpConnector connector in running)
            {
                connector.Abort();
            }
        }

        internal State CheckMachine(SSHConnectionSettings connection)
        {
            // The same split as the connection itself uses: a bare IPv6 address ("::1") has no port, and "[::1]:2222" has one.
            // Cutting at the first colon made int.Parse throw for every IPv6 host in the list.
            string host = SSHConnectionSettings.SplitHostPort(connection.HostName, out int? port);
            return CheckMachine(host, port ?? 22);
        }
    }

    public enum SubtypeImage
    {
        None,
        Lock,
        OpenLock,
        SerialPort,
        USB,
        Terminal
    }

    public interface IConnectionSettingsWrapper
    {
        Uri Image { get; }

        SubtypeImage TypeImage { get; }

        string Title { get; }

        string Detail1 { get; }

        string Detail2 { get; }

        bool IsInactive { get; set; }

        bool HasBaudRate { get; }

        int BaudRate { get; set; }

        long LastUseTime { get; }

        bool MatchesFilter(string filter);

        void Delete();

        void Edit(Window wnd);

        void SetIcon(string icon);
    }

    private class SettingsWrappers
    {
        public class SSH : IConnectionSettingsWrapper, INotifyPropertyChanged
        {
            private readonly SavedConnectionStore _Manager;

            public readonly SSHConnectionSettings Connection;

            private bool _IsInactive;

            public Uri Image => MakeIconURI(Connection.Icon);

            public SubtypeImage TypeImage
            {
                get
                {
                    if (Connection.AuthenticationType != AuthType.Password)
                    {
                        return SubtypeImage.OpenLock;
                    }
                    return SubtypeImage.Lock;
                }
            }

            public long LastUseTime => Connection.LastConnectionTime;

            public string Title => Connection.ConnectionAlias ?? Connection.ToString();

            public string Detail1 => Connection.AuthenticationType switch
            {
                AuthType.OpenSSHKey => "SSH, OpenSSH key",
                AuthType.Password => "SSH, password",
                AuthType.PublicKey => "SSH, private key",
                _ => "SSH, unknown",
            };

            public bool IsInactive
            {
                get
                {
                    return _IsInactive;
                }
                set
                {
                    _IsInactive = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsInactive"));
                }
            }

            public string Detail2 => FormatLastUsedString(DateTime.FromFileTime(Connection.LastConnectionTime));

            public bool HasBaudRate => false;

            public int BaudRate { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;

            public SSH(SavedConnectionStore mgr, SSHConnectionSettings conn)
            {
                _Manager = mgr;
                Connection = conn;
            }

            public void Delete()
            {
                _Manager?.DeleteSshConnection(Connection);
            }

            public void Edit(Window wnd)
            {
                NewConnectionController.WindowCustomization customization = new NewConnectionController.WindowCustomization
                {
                    Caption = "Edit an SSH Connection",
                    ShowAliasField = true,
                    HeaderText = "SSH Connection Settings",
                    ConnectButtonText = "Save",
                    ShowSaveOnlyButton = true
                };
                NewSSHConnectionWindow newSSHConnectionWindow = new NewSSHConnectionWindow(Connection, customization);
                newSSHConnectionWindow.Owner = wnd;
                newSSHConnectionWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                if (newSSHConnectionWindow.ShowDialog() == true && (newSSHConnectionWindow.Controller.SavedWithoutConnecting || newSSHConnectionWindow.Controller.CreatedSession != null))
                {
                    _Manager?.SaveSshConnection(newSSHConnectionWindow.Controller.FinalSettings);
                    newSSHConnectionWindow.Controller.CreatedSession?.Dispose();
                }
            }

            public bool MatchesFilter(string filter)
            {
                SSHConnectionSettings connection = Connection;
                int index = filter.IndexOf('@');
                if (index == -1)
                {
                    if (connection.ConnectionAlias != null && connection.ConnectionAlias.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) != -1)
                    {
                        return true;
                    }
                    if (connection.HostName == null || connection.HostName.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) == -1)
                    {
                        if (connection.UserName != null)
                        {
                            return connection.UserName.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) != -1;
                        }
                        return false;
                    }
                    return true;
                }
                string value = filter.Substring(0, index);
                string substring = filter.Substring(index + 1);
                if (connection.HostName != null && connection.HostName.IndexOf(substring, StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    if (connection.UserName != null)
                    {
                        return connection.UserName.IndexOf(value, StringComparison.InvariantCultureIgnoreCase) != -1;
                    }
                    return false;
                }
                return false;
            }

            public void SetIcon(string icon)
            {
                Connection.Icon = icon;
                // A connection opened from the command line or a job has no settings file to update.
                if (Connection.FileName != null)
                {
                    Connection.Save();
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Image"));
            }
        }

        public class Raw : INotifyPropertyChanged
        {
            public readonly RawConnectionSettings Settings;

            private readonly SavedConnectionStore _Manager;

            public event PropertyChangedEventHandler PropertyChanged;

            public Raw(SavedConnectionStore mgr, RawConnectionSettings settings)
            {
                Settings = settings;
                _Manager = mgr;
            }

            public void Delete()
            {
                _Manager.DeleteRawConnection(Settings);
            }

            public void Edit(Window wnd)
            {
                RawConnectionSettingsWindow rawConnectionSettingsWindow = new RawConnectionSettingsWindow(Settings.Settings, testOnOK: false, showSaveCheckbox: false);
                rawConnectionSettingsWindow.Owner = wnd;
                RawConnectionSettingsWindow rawConnectionSettingsWindow2 = rawConnectionSettingsWindow;
                if (rawConnectionSettingsWindow2.ShowDialog() == true)
                {
                    Settings.Settings = rawConnectionSettingsWindow2.Settings;
                    _Manager.SaveRawConnection(Settings);
                }
            }

            public void SetIcon(string icon)
            {
                Settings.Icon = icon;
                // The store gives the settings a file name: a COM port that was never connected has none yet.
                _Manager.SaveRawConnection(Settings);
                OnPropertyChanged("Image");
            }

            protected void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public class SerialPort : Raw, IConnectionSettingsWrapper
        {
            private SetupApiDeviceSet.ComPortInfo _Port;

            private readonly string _SerialNumber;

            public Uri Image => MakeIconURI(Settings.Icon ?? "SerialPort.png");

            public long LastUseTime => 0L;

            public SubtypeImage TypeImage
            {
                get
                {
                    if (!string.IsNullOrEmpty(_SerialNumber))
                    {
                        return SubtypeImage.USB;
                    }
                    return SubtypeImage.None;
                }
            }

            public string Title => $"{_Port.Description} (COM{_Port.PortNumber})";

            public string Detail1
            {
                get
                {
                    if (!string.IsNullOrEmpty(_SerialNumber))
                    {
                        return "USB S/N: " + _SerialNumber;
                    }
                    return null;
                }
            }

            public string Detail2 => null;

            public bool HasBaudRate => true;

            public int BaudRate
            {
                get
                {
                    return ((Settings.Settings.Connection as DeviceTerminalSettings.ConnectionSettings.Serial)?.AdvancedSettings?.BaudRate).GetValueOrDefault();
                }
                set
                {
                    DeviceTerminalSettings.ConnectionSettings.Serial serial = Settings.Settings.Connection as DeviceTerminalSettings.ConnectionSettings.Serial;
                    if (serial == null)
                    {
                        serial = (DeviceTerminalSettings.ConnectionSettings.Serial)(Settings.Settings.Connection = new DeviceTerminalSettings.ConnectionSettings.Serial());
                    }
                    if (serial.AdvancedSettings == null)
                    {
                        serial.AdvancedSettings = new DeviceTerminalSettings.ConnectionSettings.Serial.AdvancedSerialPortSettings();
                    }
                    serial.AdvancedSettings.BaudRate = value;
                }
            }

            public bool IsInactive
            {
                get
                {
                    return false;
                }
                set
                {
                }
            }

            public SerialPort(SavedConnectionStore mgr, SetupApiDeviceSet.ComPortInfo port, RawConnectionSettings settings, string serialNumber)
                : base(mgr, settings)
            {
                _Port = port;
                _SerialNumber = serialNumber;
            }

            public bool MatchesFilter(string filter)
            {
                if ((_SerialNumber?.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) ?? (-1)) != -1)
                {
                    return true;
                }
                if (Title.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    return true;
                }
                return false;
            }
        }

        public class RawTcp : Raw, IConnectionSettingsWrapper, INotifyPropertyChanged
        {
            private bool _IsInactive;

            public bool IsInactive
            {
                get
                {
                    return _IsInactive;
                }
                set
                {
                    _IsInactive = value;
                    OnPropertyChanged("IsInactive");
                }
            }

            public Uri Image => MakeIconURI(Settings.Icon);

            public SubtypeImage TypeImage => SubtypeImage.Terminal;

            public string Title => (Settings.Settings.Connection as DeviceTerminalSettings.ConnectionSettings.Tcp)?.ToString() ?? "";

            public virtual string Detail1 => "raw tcp";

            public string Detail2 => FormatLastUsedString(DateTime.FromFileTime(Settings.Settings.LastConnectionTime));

            public bool HasBaudRate => false;

            public int BaudRate { get; set; }

            public long LastUseTime => Settings.Settings.LastConnectionTime;

            public RawTcp(SavedConnectionStore mgr, RawConnectionSettings settings)
                : base(mgr, settings)
            {
            }

            public bool MatchesFilter(string filter)
            {
                if (Title.IndexOf(filter, StringComparison.InvariantCultureIgnoreCase) != -1)
                {
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// A saved telnet connection. It is a raw TCP item with a different kind label: it is listed, probed,
        /// edited and deleted like one, but counted and filtered separately (check for it before RawTcp).
        /// </summary>
        public class Telnet : RawTcp
        {
            public override string Detail1 => "telnet";

            public Telnet(SavedConnectionStore mgr, RawConnectionSettings settings)
                : base(mgr, settings)
            {
            }
        }
    }

    public class ControllerImpl : INotifyPropertyChanged, IDisposable
    {
        public struct ConnectionCountInfo
        {
            public int SSH { get; set; }

            public int RawTcp { get; set; }

            public int Telnet { get; set; }

            public int Serial { get; set; }
        }

        public struct SerialPortKey
        {
            public string Description;

            public int Number;

            public SerialPortKey(DeviceTerminalSettings.ConnectionSettings.Serial serConn)
            {
                Description = serConn.DeviceDescription;
                string comPortName = serConn.ComPortName;
                if (comPortName != null && comPortName.StartsWith("COM", StringComparison.InvariantCultureIgnoreCase) && int.TryParse(serConn.ComPortName.Substring(3), out var comPortNumber))
                {
                    Number = comPortNumber;
                }
                else
                {
                    Number = 0;
                }
            }

            public SerialPortKey(SetupApiDeviceSet.ComPortInfo port)
            {
                Description = port.Description;
                Number = port.PortNumber;
            }
        }

        private readonly Dispatcher _Dispatcher;

        private bool _ShowRawConnections;

        private MachineConnectionTester _ConnectionTester = new MachineConnectionTester();

        private SshConnector _Initiator = new SshConnector();

        private SavedConnectionStore _Connections = new SavedConnectionStore();

        private List<IConnectionSettingsWrapper> _StaticItems = new List<IConnectionSettingsWrapper>();

        private List<IConnectionSettingsWrapper> _DynamicItems = new List<IConnectionSettingsWrapper>();

        private ManualResetEvent _ShutdownEvent = new ManualResetEvent(initialState: false);

        private ConnectionSortMode _CurrentSortMode;

        private ShownConnectionTypes _ShownTypes;

        private string _ProgressText;

        private string _Filter;

        // Replaced, never changed in place, so the port list thread can read it without a lock.
        private volatile Dictionary<SerialPortKey, RawConnectionSettings> _KnownSerialPortSettings = new Dictionary<SerialPortKey, RawConnectionSettings>();

        // 1 when the saved COM profiles changed since the port list thread last built its items.
        private int _KnownSerialPortSettingsChanged;

        private bool _ProbeConnections;

        private BitmapImage[] _CachedIcons;

        public readonly SmarTTYJob Job;

        public List<IConnectionSettingsWrapper> FilteredSettings { get; private set; } = new List<IConnectionSettingsWrapper>();

        public ConnectionSortMode CurrentSortMode
        {
            get
            {
                return _CurrentSortMode;
            }
            set
            {
                _CurrentSortMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CurrentSortMode"));
                UpdateFilteredItems();
            }
        }

        public ConnectionCountInfo ConnectionCounts { get; private set; }

        public string ProgressText
        {
            get
            {
                return _ProgressText;
            }
            set
            {
                _ProgressText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressText"));
            }
        }

        public string OverlayText { get; private set; }

        public DeviceTerminalSettings PendingRawConnectionSettings { get; private set; }

        /// <summary>
        /// Returns the COM/TCP connection chosen by the user and forgets it, so that a later SSH connection made
        /// with the same controller is not mistaken for it.
        /// </summary>
        public DeviceTerminalSettings TakePendingRawConnectionSettings()
        {
            DeviceTerminalSettings pending = PendingRawConnectionSettings;
            PendingRawConnectionSettings = null;
            return pending;
        }

        public string Filter
        {
            get
            {
                return _Filter;
            }
            set
            {
                _Filter = value;
                UpdateFilteredItems();
            }
        }

        public SshSession CreatedSession { get; private set; }

        public SSHConnectionSettings SelectedSettings { get; private set; }

        public bool ShowSerial
        {
            get
            {
                return _ShownTypes.Serial;
            }
            set
            {
                _ShownTypes.Serial = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowSerial"));
                UpdateFilteredItems();
            }
        }

        public bool ShowSSH
        {
            get
            {
                return _ShownTypes.SSH;
            }
            set
            {
                _ShownTypes.SSH = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowSSH"));
                UpdateFilteredItems();
            }
        }

        public bool ShowRawTcp
        {
            get
            {
                return _ShownTypes.RawTcp;
            }
            set
            {
                _ShownTypes.RawTcp = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowRawTcp"));
                UpdateFilteredItems();
            }
        }

        public bool ShowTelnet
        {
            get
            {
                return !_ShownTypes.HideTelnet;
            }
            set
            {
                _ShownTypes.HideTelnet = !value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowTelnet"));
                UpdateFilteredItems();
            }
        }

        public ConnectionSortMode[] SortModes { get; } = new ConnectionSortMode[2]
        {
            ConnectionSortMode.Name,
            ConnectionSortMode.LastUse
        };

        public bool ProbeConnections
        {
            get
            {
                return _ProbeConnections;
            }
            set
            {
                _ProbeConnections = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProbeConnections"));
                UpdateFilteredItems();
            }
        }

        public BitmapImage[] AllIcons
        {
            get
            {
                if (_CachedIcons == null)
                {
                    List<BitmapImage> list = new List<BitmapImage>();
                    string[] embeddedIcons = new string[]
                    {
                        "computer.png", "computer-laptop.png", "server.png", "vds.png", "cloud.png", "router.png",
                        "switch.png", "access-point.png", "firewall.png", "nas.png", "database.png", "container.png",
                        "terminal.png", "single-board.png", "chip.png", "SerialPort.png", "ip-camera.png", "phone.png"
                    };
                    foreach (string iconName in embeddedIcons)
                    {
                        try
                        {
                            BitmapImage bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.UriSource = new Uri("pack://application:,,,/icons/" + iconName, UriKind.Absolute);
                            bitmapImage.EndInit();
                            list.Add(bitmapImage);
                        }
                        catch
                        {
                        }
                    }
                    _CachedIcons = list.ToArray();
                }
                return _CachedIcons;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public event Action SessionCreated;

        private void UpdateThreadBody()
        {
            SetupApiDeviceSet.ComPortInfo[] second = new SetupApiDeviceSet.ComPortInfo[0];
            do
            {
                List<IConnectionSettingsWrapper> list = new List<IConnectionSettingsWrapper>();
                try
                {
                    SetupApiDeviceSet.ComPortInfo[] items;
                    // Checked twice a second: the device list must be freed every time, not left to pile up.
                    using (SetupApiDeviceSet deviceSet = new SetupApiDeviceSet(new Guid("4d36e978-e325-11ce-bfc1-08002be10318")))
                    {
                        items = deviceSet.EnumerateComPorts().ToArray();
                    }
                    // The list is also rebuilt when the saved COM profiles changed, or an item would keep a deleted or
                    // outdated profile until a port is plugged in or out.
                    bool settingsChanged = Interlocked.Exchange(ref _KnownSerialPortSettingsChanged, 0) != 0;
                    if (items.SequenceEqual(second) && !settingsChanged)
                    {
                        continue;
                    }
                    second = items;
                    Dictionary<SerialPortKey, RawConnectionSettings> knownSettings = _KnownSerialPortSettings;
                    SetupApiDeviceSet.ComPortInfo[] entries = items;
                    for (int portIndex = 0; portIndex < entries.Length; portIndex++)
                    {
                        SetupApiDeviceSet.ComPortInfo port = entries[portIndex];
                        string serialNumber = SetupApiDeviceSet.GetUsbSerialNumber(port.InstanceId, out var item);
                        if (!item)
                        {
                            serialNumber = null;
                        }
                        knownSettings.TryGetValue(new SerialPortKey(port), out var value);
                        if (value == null)
                        {
                            // Profiles saved without the device description are keyed by the port number alone.
                            knownSettings.TryGetValue(new SerialPortKey { Number = port.PortNumber }, out value);
                        }
                        if (value == null)
                        {
                            RawConnectionSettings rawConnectionSettings = new RawConnectionSettings();
                            DeviceTerminalSettings deviceTerminalSettings = new DeviceTerminalSettings();
                            DeviceTerminalSettings.ConnectionSettings.Serial serial = new DeviceTerminalSettings.ConnectionSettings.Serial();
                            int portNumber = port.PortNumber;
                            serial.ComPortName = "COM" + portNumber;
                            // Saved with the profile (an icon, for instance), it lets the next scan find the profile again.
                            serial.DeviceDescription = port.Description;
                            deviceTerminalSettings.Connection = serial;
                            rawConnectionSettings.Settings = deviceTerminalSettings;
                            value = rawConnectionSettings;
                        }
                        list.Add(new SettingsWrappers.SerialPort(_Connections, port, value, serialNumber));
                    }
                }
                catch
                {
                }
                _DynamicItems = list;
                _Dispatcher.BeginInvoke((Delegate)(Action)(() =>
                {
                    UpdateFilteredItems();
                }), new object[0]);
            }
            while (!_ShutdownEvent.WaitOne(500));
        }

        public ControllerImpl(Dispatcher dispatcher, bool showRawConnections, string defaultItemName, CommonGUIPreferences prefs, bool suppressUpdateThread = false)
        {
            ControllerImpl controllerImpl = this;
            _Dispatcher = dispatcher;
            _ShowRawConnections = showRawConnections;
            _ConnectionTester.BackgroundCheckCompleted += () =>
            {
                dispatcher.BeginInvoke((Delegate)(Action)(() =>
                {
                    controllerImpl.UpdateFilteredItems();
                }), new object[0]);
            };
            _ShownTypes = prefs.ShownConnections;
            _ProbeConnections = prefs.ProbeConnections;
            CurrentSortMode = prefs.SortMode;
            RefreshAll();
            if (!suppressUpdateThread)
            {
                new Thread(UpdateThreadBody).Start();
            }
            // Without it the connector runs its callbacks (progress, error message boxes, credential prompts, the new session)
            // on its worker thread instead of the UI thread.
            _Initiator.Dispatcher = dispatcher;
            _Initiator.StatusChanged += _Initiator_ConnectionProgress;
            _Initiator.CredentialsRequested += _Initiator_PasswordRequest;
            _Initiator.ProxyCredentialsRequested += _Initiator_ProxyAuthRequest;
            _Initiator.Connected += _Initiator_SessionCreated;
            if (string.IsNullOrEmpty(defaultItemName))
            {
                return;
            }
            if (defaultItemName.StartsWith("/ssh:", StringComparison.InvariantCultureIgnoreCase))
            {
                SSHConnectionSettings conn = XmlStorage.LoadFromFile<SSHConnectionSettings>(defaultItemName.Substring(5), new Type[0]);
                ConnectWithSettings(new SettingsWrappers.SSH(_Connections, conn));
                return;
            }
            if (defaultItemName.StartsWith("/telnets:", StringComparison.InvariantCultureIgnoreCase))
            {
                // /telnets:host or /telnets:host:port - telnet over TLS (port 992 by default), not saved anywhere.
                try
                {
                    PendingRawConnectionSettings = NewTelnetSettings(DeviceTerminalSettings.ConnectionSettings.Telnet.Parse("telnets://" + defaultItemName.Substring("/telnets:".Length)));
                }
                catch (FormatException exception)
                {
                    MessageBoxes.Exception(exception);
                }
                return;
            }
            if (defaultItemName.StartsWith("/telnet:", StringComparison.InvariantCultureIgnoreCase))
            {
                // /telnet:host or /telnet:host:port - a telnet connection that is not saved anywhere.
                try
                {
                    PendingRawConnectionSettings = NewTelnetSettings(DeviceTerminalSettings.ConnectionSettings.Telnet.Parse(defaultItemName.Substring("/telnet:".Length)));
                }
                catch (FormatException exception)
                {
                    MessageBoxes.Exception(exception);
                }
                return;
            }
            if (defaultItemName.StartsWith("/job:", StringComparison.InvariantCultureIgnoreCase))
            {
                ConnectWithSettings(new SettingsWrappers.SSH(conn: (Job = XmlStorage.LoadFromFile<SmarTTYJob>(defaultItemName.Substring(5), new Type[0])).Settings, mgr: _Connections));
                return;
            }
            IConnectionSettingsWrapper connectionSettingsWrapper = FindByCommandLine(defaultItemName);
            if (connectionSettingsWrapper != null)
            {
                ConnectWithSettings(connectionSettingsWrapper);
            }
        }

        /// <summary>
        /// Looks up the saved connection named on the command line. A title matches first, exactly as before; a
        /// <c>user@host</c> argument that names no saved entry directly is then looked up by host and user (and by
        /// host alone), so a profile saved under another name or case can still be opened the way <c>ssh</c> does.
        /// </summary>
        private IConnectionSettingsWrapper FindByCommandLine(string name)
        {
            IConnectionSettingsWrapper byTitle = FilteredSettings.FirstOrDefault((IConnectionSettingsWrapper candidate) => candidate.Title == name);
            if (byTitle != null)
            {
                return byTitle;
            }
            SSHConnectionSettings connection = _Connections.FindSshConnectionForArgument(name);
            return connection == null ? null : new SettingsWrappers.SSH(_Connections, connection);
        }

        private void UpdateMachineStates(IEnumerable<IConnectionSettingsWrapper> settings)
        {
            foreach (IConnectionSettingsWrapper setting in settings)
            {
                if (setting is SettingsWrappers.SSH sSH && !sSH.Connection.SuppressAutoProbing)
                {
                    sSH.IsInactive = _ConnectionTester.CheckMachine(sSH.Connection) != MachineConnectionTester.State.Succeeded;
                }
                else if (setting is SettingsWrappers.RawTcp rawTcp && rawTcp.Settings.Settings.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp tcp)
                {
                    rawTcp.IsInactive = _ConnectionTester.CheckMachine(tcp.Host, tcp.Port) != MachineConnectionTester.State.Succeeded;
                }
            }
        }

        private void _Initiator_SessionCreated(SshConnector sender, SshSession connectedSession, SSHConnectionSettings settings)
        {
            try
            {
                ProgressText = null;
                CreatedSession = connectedSession;
                SelectedSettings = settings;
                SessionCreated?.Invoke();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        private void _Initiator_ProxyAuthRequest(SshConnector sender, string realm, ref string auth, ref bool handled)
        {
            handled = ProxyAuthWindow.ShowPrompt(WindowOwnership.ActiveWindow(), realm, ref auth);
        }

        private void _Initiator_PasswordRequest(SshConnector sender, SSHConnectionSettings settings, ref string password, ref bool handled, bool requestingPassphrase)
        {
            handled = PasswordRequestWindow.ShowPrompt(WindowOwnership.ActiveWindow(), settings, ref password, requestingPassphrase, isSudo: false);
        }

        private void _Initiator_ConnectionProgress(SshConnector sender, string text, Exception exception)
        {
            if (exception == null)
            {
                ProgressText = text ?? "Connecting...";
                return;
            }
            // Hide the progress first and show the (modal) message after the current layout/render pass: a modal loop
            // started from inside a binding update re-enters WPF's layout while the progress animation is running.
            ProgressText = null;
            if (!(exception is OperationCanceledException))
            {
                _Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)(() => MessageBoxes.Exception(exception)));
            }
        }

        private void UpdateFilteredItems()
        {
            ConnectionCountInfo connectionCounts = default;
            foreach (IConnectionSettingsWrapper item in _StaticItems.Concat(_DynamicItems))
            {
                if (item is SettingsWrappers.SSH)
                {
                    connectionCounts.SSH++;
                }
                else if (item is SettingsWrappers.SerialPort)
                {
                    connectionCounts.Serial++;
                }
                else if (item is SettingsWrappers.Telnet)
                {
                    connectionCounts.Telnet++;
                }
                else if (item is SettingsWrappers.RawTcp)
                {
                    connectionCounts.RawTcp++;
                }
            }
            ConnectionCounts = connectionCounts;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ConnectionCounts"));
            List<IConnectionSettingsWrapper> list = (from connectionSettingsWrapper in _StaticItems.Concat(_DynamicItems).Where(ShouldShowThisType)
                                                     where string.IsNullOrEmpty(_Filter) || connectionSettingsWrapper.MatchesFilter(_Filter)
                                                     select connectionSettingsWrapper).ToList();
            if (ProbeConnections)
            {
                UpdateMachineStates(list);
            }
            else
            {
                foreach (IConnectionSettingsWrapper entry in list)
                {
                    entry.IsInactive = false;
                }
            }
            list.Sort(CompareConnections);
            FilteredSettings = list;
            if (FilteredSettings.Count == 0)
            {
                if (string.IsNullOrEmpty(_Filter))
                {
                    OverlayText = "No connections found. Use the buttons below to create new connections.";
                }
                else
                {
                    OverlayText = "No connections match '" + _Filter + "'.";
                }
            }
            else
            {
                OverlayText = null;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverlayText"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FilteredSettings"));
        }

        private int CompareConnections(IConnectionSettingsWrapper x, IConnectionSettingsWrapper y)
        {
            if (x.IsInactive != y.IsInactive)
            {
                if (x.IsInactive)
                {
                    return 1;
                }
                return -1;
            }
            ConnectionSortMode currentSortMode = CurrentSortMode;
            if (currentSortMode != ConnectionSortMode.Name && currentSortMode == ConnectionSortMode.LastUse)
            {
                return -Math.Sign(x.LastUseTime - y.LastUseTime);
            }
            return StringComparer.InvariantCulture.Compare(x.Title, y.Title);
        }

        private bool ShouldShowThisType(IConnectionSettingsWrapper item)
        {
            if (item is SettingsWrappers.SSH)
            {
                return ShowSSH;
            }
            if (item is SettingsWrappers.SerialPort)
            {
                return ShowSerial;
            }
            if (item is SettingsWrappers.Telnet)
            {
                return ShowTelnet;
            }
            if (item is SettingsWrappers.RawTcp)
            {
                return ShowRawTcp;
            }
            return true;
        }

        internal void RefreshAll()
        {
            _StaticItems.Clear();
            foreach (SSHConnectionSettings item in _Connections.SshConnections)
            {
                _StaticItems.Add(new SettingsWrappers.SSH(_Connections, item));
            }
            if (_ShowRawConnections)
            {
                // Built aside and swapped in whole: the port list thread reads it meanwhile.
                Dictionary<SerialPortKey, RawConnectionSettings> knownSettings = new Dictionary<SerialPortKey, RawConnectionSettings>();
                foreach (RawConnectionSettings entry in _Connections.RawConnections)
                {
                    if (entry.Settings?.Connection is DeviceTerminalSettings.ConnectionSettings.Serial serConn)
                    {
                        knownSettings[new SerialPortKey(serConn)] = entry;
                    }
                    else if (entry.Settings?.Connection is DeviceTerminalSettings.ConnectionSettings.Telnet)
                    {
                        _StaticItems.Add(new SettingsWrappers.Telnet(_Connections, entry));
                    }
                    else if (entry.Settings?.Connection is DeviceTerminalSettings.ConnectionSettings.Tcp)
                    {
                        _StaticItems.Add(new SettingsWrappers.RawTcp(_Connections, entry));
                    }
                }
                _KnownSerialPortSettings = knownSettings;
                Interlocked.Exchange(ref _KnownSerialPortSettingsChanged, 1);
            }
            UpdateFilteredItems();
        }

        public void ConnectWithSettings(IConnectionSettingsWrapper wrp)
        {
            if (!(wrp is SettingsWrappers.SSH sSH))
            {
                if (wrp is SettingsWrappers.Raw raw)
                {
                    PendingRawConnectionSettings = raw.Settings.Settings;
                    _Connections.SaveRawConnection(new RawConnectionSettings
                    {
                        Settings = PendingRawConnectionSettings
                    });
                    raw.Settings.UpdateLastUsedTime();
                    SessionCreated?.Invoke();
                }
            }
            else
            {
                BeginSSHConnection(sSH.Connection);
            }
        }

        public void BeginSSHConnection(SSHConnectionSettings connection)
        {
            // The key is set up only when a password login replaced a key that the server did not accept: SshConnector
            // raises that flag itself. Passing true for every key profile re-checked (and re-installed) the key on each
            // connection and reconnect, and a failed check asked about the password although the key login had worked.
            _Initiator.ConnectAsync(connection, null, forcePasswordAuth: false);
        }

        internal bool TryCancel()
        {
            if (ProgressText == null)
            {
                return false;
            }
            try
            {
                _Initiator.CancelConnection();
            }
            catch
            {
            }
            ProgressText = null;
            return true;
        }

        /// <summary>Whether the last <see cref="CreateNewSSHConnection"/> only saved the connection to the list.</summary>
        public bool SavedWithoutConnecting { get; private set; }

        public bool CreateNewSSHConnection(Window wnd)
        {
            NewConnectionController.WindowCustomization customization = new NewConnectionController.WindowCustomization
            {
                ShowAliasField = true,
                ShowSaveOnlyButton = true
            };
            SavedWithoutConnecting = false;
            NewSSHConnectionWindow newSSHConnectionWindow = new NewSSHConnectionWindow(null, customization);
            newSSHConnectionWindow.Owner = wnd;
            newSSHConnectionWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            bool accepted = newSSHConnectionWindow.ShowDialog() == true;
            if (accepted && newSSHConnectionWindow.Controller.SavedWithoutConnecting)
            {
                // Saved to the list as entered: there is no session to open, so the list window stays.
                _Connections.SaveSshConnection(newSSHConnectionWindow.Controller.FinalSettings);
                SavedWithoutConnecting = true;
                return false;
            }
            if (accepted && newSSHConnectionWindow.Controller.CreatedSession != null)
            {
                if (newSSHConnectionWindow.Controller.SaveSettings)
                {
                    _Connections.SaveSshConnection(newSSHConnectionWindow.Controller.FinalSettings);
                }
                CreatedSession = newSSHConnectionWindow.Controller.CreatedSession;
                SelectedSettings = newSSHConnectionWindow.Controller.FinalSettings;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            _ShutdownEvent.Set();
            _ConnectionTester.Dispose();
        }

        public bool CreateNewRawTcpConnection(Window wnd)
        {
            return CreateNewTcpConnection(wnd, new DeviceTerminalSettings
            {
                Connection = new DeviceTerminalSettings.ConnectionSettings.Tcp()
            });
        }

        public bool CreateNewTelnetConnection(Window wnd)
        {
            return CreateNewTcpConnection(wnd, NewTelnetSettings(new DeviceTerminalSettings.ConnectionSettings.Telnet
            {
                Port = DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultPort,
                TerminalSpeed = DeviceTerminalSettings.ConnectionSettings.Telnet.DefaultTerminalSpeed
            }));
        }

        // A telnet session ends a line with CR LF; the server normally echoes what is typed, so local echo stays off.
        private static DeviceTerminalSettings NewTelnetSettings(DeviceTerminalSettings.ConnectionSettings.Telnet connection)
        {
            return new DeviceTerminalSettings
            {
                Connection = connection,
                LineEnding = DeviceTerminalSettings.LineEndingType.CRLF
            };
        }

        private bool CreateNewTcpConnection(Window wnd, DeviceTerminalSettings initialSettings)
        {
            RawConnectionSettingsWindow rawConnectionSettingsWindow = new RawConnectionSettingsWindow(initialSettings, testOnOK: true, showSaveCheckbox: true);
            rawConnectionSettingsWindow.Owner = wnd;
            RawConnectionSettingsWindow rawConnectionSettingsWindow2 = rawConnectionSettingsWindow;
            if (rawConnectionSettingsWindow2.ShowDialog() != true)
            {
                return false;
            }
            PendingRawConnectionSettings = rawConnectionSettingsWindow2.Settings;
            if (rawConnectionSettingsWindow2.SaveSettings)
            {
                _Connections.SaveRawConnection(new RawConnectionSettings
                {
                    Settings = rawConnectionSettingsWindow2.Settings
                });
            }
            return true;
        }

        internal void ExportSettings(ref CommonGUIPreferences prefs)
        {
            prefs.ShownConnections = _ShownTypes;
            prefs.ProbeConnections = ProbeConnections;
        }

        internal void CreateQuickConnection(Window parent, SmarTTYTheme theme)
        {
            QuickConnectionWindow quickConnectionWindow = new QuickConnectionWindow();
            quickConnectionWindow.Owner = parent;
            QuickConnectionWindow quickConnectionWindow2 = quickConnectionWindow;
            if (quickConnectionWindow2.ShowDialog() != true)
            {
                return;
            }
            try
            {
                string text = quickConnectionWindow2.txtMachine.Text;
                int index = text.IndexOf('@');
                if (index == -1)
                {
                    throw new Exception("Expected: username@hostname");
                }
                string userName = text.Substring(0, index);
                text = text.Substring(index + 1);
                SSHConnectionSettings sSHConnectionSettings = new SSHConnectionSettings
                {
                    HostName = text,
                    UserName = userName,
                    AuthenticationType = AuthType.Password
                };
                _Initiator.ConnectAsync(sSHConnectionSettings, quickConnectionWindow2.txtPassword.Password, forcePasswordAuth: false);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }

        /// <summary>
        /// Telnet to "host" or "host:port" without saving anything (port 23 when none is given). A login can come first:
        /// "user@host", "user:password@host:port" (typed when the server asks for it, never saved).
        /// </summary>
        internal void CreateQuickTelnetConnection(Window parent)
        {
            QuickConnectionWindow quickConnectionWindow = new QuickConnectionWindow(telnet: true);
            quickConnectionWindow.Owner = parent;
            if (quickConnectionWindow.ShowDialog() != true)
            {
                return;
            }
            try
            {
                PendingRawConnectionSettings = NewTelnetSettings(DeviceTerminalSettings.ConnectionSettings.Telnet.ParseWithLogin(quickConnectionWindow.txtMachine.Text));
                SessionCreated?.Invoke();
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }
    }

    public readonly ControllerImpl Controller;

    private ToggleButton _ActiveIconPicker;

    // Set while the icon list marks the current icon of the connection: that is not a choice of the user.
    private bool _SelectingCurrentIcon;

    private SmarTTYTheme _Theme;

    public ConnectionSelectionWindow(bool showRawConnections, string defaultItemNameOrCmdLine)
    {
        InitializeComponent();
        GlobalSettings savedSettings = GlobalSettings.Load();
        _Theme = savedSettings?.Theme ?? SmarTTYTheme.Dark;
        ThemeManager.Apply(savedSettings);
        this.DataContext = (Controller = new ControllerImpl(this.Dispatcher, showRawConnections, defaultItemNameOrCmdLine, GuiPreferencesStore.Current));
        Controller.SessionCreated += Controller_SessionCreated;
        if (Controller.PendingRawConnectionSettings != null)
        {
            // A COM/TCP/telnet connection named on the command line is already chosen (the controller could not tell
            // the window yet): there is nothing left to ask, so the window closes as soon as it is shown.
            Loaded += (sender, args) => Close();
        }
        try
        {
            GuiPreferencesStore.Current?.ConnectionWindow.Apply((Window)this);
        }
        catch
        {
        }
    }

    private void Controller_SessionCreated()
    {
        this.Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            this.Close();
        }), new object[0]);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CommonGUIPreferences prefs = GuiPreferencesStore.Current;
        prefs?.ConnectionWindow.Save((Window)this);
        Controller.ExportSettings(ref prefs);
        Controller.Dispose();
        GuiPreferencesStore.Save();
    }

    public ConnectionSelectionWindow()
        : this(showRawConnections: true, null)
    {
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((int)e.Key == 6 && ((Selector)connectionList).SelectedItem is IConnectionSettingsWrapper wrp)
        {
            try
            {
                Controller.ConnectWithSettings(wrp);
            }
            catch (Exception exception)
            {
                MessageBoxes.Exception(exception);
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private static string FormatLastUsedString(DateTime lastUseTime)
    {
        DateTime now = DateTime.Now;
        if (lastUseTime.Date == now.Date)
        {
            return "Last used: today";
        }
        int totalDays = (int)(now.Date - lastUseTime.Date).TotalDays;
        if (totalDays == 1)
        {
            return "Last used: yesterday";
        }
        if (totalDays < 365)
        {
            return $"Last used: {totalDays} days ago";
        }
        return "Last used: over a year ago";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        object buttonElement = ((sender is Button) ? sender : null);
        if (!(((buttonElement != null) ? ((FrameworkElement)buttonElement).DataContext : null) is IConnectionSettingsWrapper connectionSettingsWrapper) || ((Control.ModifierKeys & Keys.Shift) != Keys.Shift && !MessageBoxes.Ask("Delete " + connectionSettingsWrapper.Title + "?\nTip: hold 'shift' to suppress this message.")))
        {
            return;
        }
        try
        {
            connectionSettingsWrapper.Delete();
            Controller.RefreshAll();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            object buttonElement = ((sender is Button) ? sender : null);
            (((buttonElement != null) ? ((FrameworkElement)buttonElement).DataContext : null) as IConnectionSettingsWrapper)?.Edit((Window)this);
            Controller.RefreshAll();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void ListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if ((int)e.Key != 32)
        {
            return;
        }
        IConnectionSettingsWrapper[] items = connectionList.SelectedItems.OfType<IConnectionSettingsWrapper>().ToArray();
        if (items.Length == 0)
        {
            return;
        }
        string text = items.First().Title;
        if (items.Length > 1)
        {
            text = $"{items.Length} items";
        }
        if (!MessageBoxes.Ask("Delete " + text + "?"))
        {
            return;
        }
        try
        {
            IConnectionSettingsWrapper[] entries = items;
            for (int settingsIndex = 0; settingsIndex < entries.Length; settingsIndex++)
            {
                entries[settingsIndex].Delete();
            }
            Controller.RefreshAll();
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void connectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((Selector)connectionList).SelectedItem is IConnectionSettingsWrapper wrp)
        {
            Controller.ConnectWithSettings(wrp);
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (!Controller.TryCancel())
        {
            this.Close();
        }
    }

    private void txtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (((CollectionView)connectionList.Items).Count == 1)
        {
            ((Selector)connectionList).SelectedItem = connectionList.Items[0];
        }
    }

    private void NewSSH_Click(object sender, RoutedEventArgs e)
    {
        if (Controller.CreateNewSSHConnection((Window)this))
        {
            this.Close();
        }
        else if (Controller.SavedWithoutConnecting)
        {
            Controller.RefreshAll();
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsArchiveDialogs.Import((Window)this) != null)
        {
            Controller.RefreshAll();
        }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsArchiveDialogs.Export((Window)this);
    }

    private void NewRawTcp_Click(object sender, RoutedEventArgs e)
    {
        if (Controller.CreateNewRawTcpConnection((Window)this))
        {
            this.Close();
        }
    }

    private void NewTelnet_Click(object sender, RoutedEventArgs e)
    {
        if (Controller.CreateNewTelnetConnection((Window)this))
        {
            this.Close();
        }
    }

    private void connectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void IconPicker_Checked(object sender, RoutedEventArgs e)
    {
        _ActiveIconPicker = sender as ToggleButton;
        if (_ActiveIconPicker == null)
        {
            return;
        }
        iconPopup.PlacementTarget = (UIElement)_ActiveIconPicker;
        iconPopup.VerticalOffset = ((FrameworkElement)_ActiveIconPicker).ActualHeight;
        iconPopup.IsOpen = true;
        _SelectingCurrentIcon = true;
        try
        {
            ((Selector)lstIcons).SelectedItem = null;
            Uri imageUri = (((FrameworkElement)_ActiveIconPicker).DataContext as IConnectionSettingsWrapper)?.Image;
            if (imageUri != null)
            {
                string text = Path.GetFileName(imageUri.OriginalString ?? imageUri.ToString()).ToLower();
                BitmapImage[] allIcons = Controller.AllIcons;
                foreach (BitmapImage bitmapImage in allIcons)
                {
                    try
                    {
                        string currentName = Path.GetFileName(bitmapImage.UriSource?.OriginalString ?? bitmapImage.UriSource?.ToString())?.ToLower();
                        if (currentName == text)
                        {
                            ((Selector)lstIcons).SelectedItem = bitmapImage;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            _SelectingCurrentIcon = false;
        }
    }

    private void iconPopup_Closed(object sender, EventArgs e)
    {
        if (_ActiveIconPicker != null)
        {
            _ActiveIconPicker.IsChecked = false;
        }
    }

    private static Uri MakeIconURI(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = "computer.png";
        }
        try
        {
            if (Path.IsPathRooted(path) && File.Exists(path))
            {
                return new Uri(path);
            }

            string fileName = Path.GetFileName(path);
            return new Uri("pack://application:,,,/icons/" + fileName, UriKind.Absolute);
        }
        catch
        {
            return new Uri("pack://application:,,,/icons/computer.png", UriKind.Absolute);
        }
    }

    private void lstIcons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_SelectingCurrentIcon)
        {
            return;
        }
        ToggleButton activeIconPicker = _ActiveIconPicker;
        if (((activeIconPicker != null) ? ((FrameworkElement)activeIconPicker).DataContext : null) is IConnectionSettingsWrapper connectionSettingsWrapper)
        {
            ImageSource imageSource = e.AddedItems.OfType<ImageSource>().FirstOrDefault();
            BitmapImage bitmapImage = imageSource as BitmapImage;
            if (bitmapImage != null && bitmapImage.UriSource != null)
            {
                string fileName = Path.GetFileName(bitmapImage.UriSource.OriginalString ?? bitmapImage.UriSource.ToString());
                connectionSettingsWrapper.SetIcon(fileName);
                iconPopup.IsOpen = false;
            }
        }
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        ToggleButton activeIconPicker = _ActiveIconPicker;
        if (((activeIconPicker != null) ? ((FrameworkElement)activeIconPicker).DataContext : null) is IConnectionSettingsWrapper connectionSettingsWrapper)
        {
            string image = LocalFileDialogWindow.OpenFile(this, "Locate a custom image...", "PNG images (*.png)|*.png");
            if (image != null)
            {
                connectionSettingsWrapper.SetIcon(image);
            }
        }
    }

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        Controller.CreateQuickConnection((Window)this, _Theme);
    }

    private void QuickTelnet_Click(object sender, RoutedEventArgs e)
    {
        Controller.CreateQuickTelnetConnection((Window)this);
    }
}
