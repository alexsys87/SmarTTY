using SmarTTY.GUI.Backend;
using SmarTTY.Ssh;
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Tools > SOCKS5 proxy over SSH: starts and stops the SOCKS5 proxy of the active SSH session (see <see cref="SocksProxyHost"/>).
/// The proxy keeps running after the window is closed, until it is stopped or the session ends.
/// </summary>
public partial class SocksProxyWindow : Window
{
    private const int MaxLogCharacters = 200000;

    private readonly SessionWrapper _Session;

    private readonly SocksProxyHost _Host;

    private readonly SocksProxySettings _Settings;

    private bool _Busy;

    public SocksProxyWindow(SessionWrapper session)
    {
        InitializeComponent();
        _Session = session;
        _Host = session.SocksProxy;
        _Settings = SocksProxySettings.Load();
        AddressBox.Text = _Host.IsRunning ? _Host.BindAddress : _Settings.BindAddress;
        PortBox.Text = (_Host.IsRunning ? _Host.Port : _Settings.Port).ToString();
        KeepAliveBox.Text = _Settings.KeepAliveSeconds.ToString();

        StringBuilder history = new StringBuilder();
        foreach (string line in _Host.GetHistory())
        {
            history.AppendLine(line);
        }
        LogBox.Text = history.ToString();
        LogBox.ScrollToEnd();

        _Host.Log += Host_Log;
        Closed += (object sender, EventArgs e) => _Host.Log -= Host_Log;
        AddressBox.TextChanged += (object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateUsage();
        PortBox.TextChanged += (object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateUsage();
        UpdateState();
    }

    private void Host_Log(string line)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (LogBox.Text.Length > MaxLogCharacters)
            {
                // Whole lines are dropped, so that the log does not start in the middle of one.
                string text = LogBox.Text;
                int cut = text.IndexOf('\n', text.Length - MaxLogCharacters / 2);
                LogBox.Text = cut < 0 ? "" : text.Substring(cut + 1);
            }
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            // The host also reports what happens without this window: the listener restored after a reconnect, a failed
            // restore. The status and the buttons follow it.
            UpdateState();
        }));
    }

    private void UpdateState()
    {
        bool running = _Host.IsRunning;
        bool idle = !_Busy;
        bool editable = !running && idle;
        AddressBox.IsEnabled = editable;
        PortBox.IsEnabled = editable;
        KeepAliveBox.IsEnabled = editable;
        StartButton.IsEnabled = editable;
        StopButton.IsEnabled = running && idle;
        if (_Busy)
        {
            StatusText.Text = "Working...";
        }
        else if (!running)
        {
            StatusText.Text = "The proxy is not running.";
        }
        else if (_Host.IsListening)
        {
            StatusText.Text = "Running: SOCKS5 proxy on " + FormatEndpoint(_Host.BindAddress, _Host.Port) + ". It works while this SSH connection is open.";
        }
        else
        {
            StatusText.Text = "Waiting for the SSH connection: the proxy is not listening now and is restored after a reconnect.";
        }
        UpdateUsage();
    }

    private void UpdateUsage()
    {
        bool running = _Host.IsRunning;
        string address = running ? _Host.BindAddress : AddressBox.Text.Trim();
        string port = running ? _Host.Port.ToString() : PortBox.Text.Trim();
        if (address == "0.0.0.0" || address == "::")
        {
            // A program of this computer reaches a proxy that listens on every address through the loopback one.
            address = (address == "::") ? "::1" : "127.0.0.1";
        }
        UsageBox.Text = "socks5h://" + (address.Contains(":") ? "[" + address + "]" : address) + ":" + port;
        CopyButton.IsEnabled = !string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(port);
    }

    private static string FormatEndpoint(string address, int port)
    {
        return (address.Contains(":") ? "[" + address + "]" : address) + ":" + port;
    }

    private bool TryGetSettings(out string address, out int port, out int keepAlive)
    {
        address = null;
        keepAlive = 0;
        port = 0;
        if (!IPAddress.TryParse(AddressBox.Text.Trim(), out IPAddress ip))
        {
            MessageBoxes.Error("Enter the local address as an IP address, e.g. 127.0.0.1.");
            AddressBox.Focus();
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out port) || port < 1 || port > 65535)
        {
            MessageBoxes.Error("The port must be a number from 1 to 65535.");
            PortBox.Focus();
            return false;
        }
        if (!int.TryParse(KeepAliveBox.Text.Trim(), out keepAlive) || keepAlive < 0 || keepAlive > 86400)
        {
            MessageBoxes.Error("The keepalive interval must be a number of seconds from 0 to 86400 (0: leave it as it is).");
            KeepAliveBox.Focus();
            return false;
        }
        address = ip.ToString();
        return true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSettings(out string address, out int port, out int keepAlive))
        {
            return;
        }
        SshSession ssh = _Session.Session;
        if (ssh == null)
        {
            MessageBoxes.Error("There is no SSH connection.");
            return;
        }
        if (!IPAddress.Parse(address).Equals(IPAddress.Loopback) && !IPAddress.Parse(address).Equals(IPAddress.IPv6Loopback)
            && !MessageBoxes.AskWarn("The proxy has no password. With " + address + " every computer that can reach this port can use the SSH server through your account as an open proxy.\n\nStart it anyway?"))
        {
            return;
        }
        _Settings.BindAddress = address;
        _Settings.Port = port;
        _Settings.KeepAliveSeconds = keepAlive;
        _Settings.Save();
        SetBusy(true);
        try
        {
            await Task.Run(() => _Host.Start(ssh, address, port, keepAlive));
        }
        catch (Exception exception)
        {
            _Host.Report("Start failed: " + exception.Message.Replace('\n', ' '));
            MessageBoxes.Exception(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await Task.Run(() => _Host.Stop());
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(UsageBox.Text);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void SetBusy(bool busy)
    {
        _Busy = busy;
        UpdateState();
    }
}
