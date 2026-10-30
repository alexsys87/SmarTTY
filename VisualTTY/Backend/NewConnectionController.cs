using SmarTTY.Ssh;
using SmarTTY.Controls;
using SmarTTY;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VisualTTY.Backend;

public class NewConnectionController : INotifyPropertyChanged
{
    public class WindowCustomization
    {
        public bool SaveCheckboxVisible = true;

        public bool BypassHostKeyCheck;

        public string Caption { get; set; } = "SmarTTY - New SSH Connection";

        public string ConnectButtonText { get; set; } = "_Connect";

        public string HeaderText { get; set; } = "Setup new SSH connection";

        public string VMNameText { get; set; }

        public bool ShowAliasField { get; set; }

        public bool AllowSavingPassword { get; set; }

        /// <summary>Offers "Save without connecting": the profile is saved as entered, without trying to connect first.</summary>
        public bool ShowSaveOnlyButton { get; set; }

        public Visibility SaveOnlyButtonVisibility => ShowSaveOnlyButton ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SaveCheckboxVisibility
        {
            get
            {
                if (SaveCheckboxVisible)
                {
                    return Visibility.Visible;
                }
                return Visibility.Collapsed;
            }
        }

        public WindowCustomization Clone()
        {
            return (WindowCustomization)MemberwiseClone();
        }
    }

    public enum BatchTransferMode
    {
        [EnumDisplayText("On-the-fly TAR")]
        Tar,
        [EnumDisplayText("File-by-file SCP (slow)")]
        SCP
    }

    private SSHConnectionSettings _Settings;

    private SshConnector _BackgroundSessionInitiator = new SshConnector();

    private Dispatcher _Dispatcher;

    private string _ConnectionAlias;

    private string _CustomSSHKeyFile;

    private string _ProgressText;

    public WindowCustomization Customization { get; }

    public bool HasAlias { get; set; }

    public string HostNameCaption { get; }

    public string ConnectionAlias
    {
        get
        {
            return _ConnectionAlias;
        }
        set
        {
            _ConnectionAlias = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ConnectionAlias"));
        }
    }

    public string HostName
    {
        get
        {
            return _Settings.HostName;
        }
        set
        {
            _Settings.HostName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HostName"));
        }
    }

    public string UserName
    {
        get
        {
            return _Settings.UserName;
        }
        set
        {
            _Settings.UserName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("UserName"));
        }
    }

    public AuthType AuthenticationType
    {
        get
        {
            return _Settings.AuthenticationType;
        }
        set
        {
            _Settings.AuthenticationType = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("AuthenticationType"));
        }
    }

    public bool HasPassphrase { get; set; }

    public string Password { get; set; }

    public string Passphrase { get; set; }

    public bool SaveSettings { get; set; } = true;

    public bool HasCustomSSHKeyFile { get; set; }

    public string CustomSSHKeyFile
    {
        get
        {
            return _CustomSSHKeyFile;
        }
        set
        {
            _CustomSSHKeyFile = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CustomSSHKeyFile"));
        }
    }

    public PublicKeyType KeyType
    {
        get
        {
            return _Settings.KeyType;
        }
        set
        {
            _Settings.KeyType = value;
        }
    }

    public bool UseProxy { get; set; }

    private SshProxyType _ProxyType;

    /// <summary>The kind of proxy; a scheme typed in the proxy field ("socks5://...") takes precedence when saving.</summary>
    public SshProxyType ProxyType
    {
        get
        {
            return _ProxyType;
        }
        set
        {
            _ProxyType = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProxyType"));
        }
    }

    public string Proxy { get; set; }

    public bool UseZlib
    {
        get
        {
            return _Settings.EnableCompression;
        }
        set
        {
            _Settings.EnableCompression = value;
        }
    }

    public bool ForwardX11
    {
        get
        {
            return _Settings.EnableX11Forwarding;
        }
        set
        {
            _Settings.EnableX11Forwarding = value;
        }
    }

    public bool AutoStartSocksProxy
    {
        get
        {
            return _Settings.AutoStartSocksProxy;
        }
        set
        {
            _Settings.AutoStartSocksProxy = value;
        }
    }

    public bool EmulateSCPWithCat
    {
        get
        {
            return _Settings.EmulateSCPWithCat;
        }
        set
        {
            _Settings.EmulateSCPWithCat = value;
        }
    }

    public bool AutoConfirmSudo
    {
        get
        {
            return _Settings.AutoConfirmSudo;
        }
        set
        {
            _Settings.AutoConfirmSudo = value;
        }
    }

    public BatchTransferMode TransferMode
    {
        get
        {
            if (!_Settings.EnableOnTheFlyTAR)
            {
                return BatchTransferMode.SCP;
            }
            return BatchTransferMode.Tar;
        }
        set
        {
            _Settings.EnableOnTheFlyTAR = value == BatchTransferMode.Tar;
        }
    }

    public bool HasKeepaliveInterval { get; set; }

    public int KeepAliveInterval { get; set; }

    public bool SetupPublicKey { get; set; } = true;

    public SSHConnectionSettings FinalSettings => _Settings;

    public SshSession CreatedSession { get; private set; }

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

    public event Action<Exception> ReportException;

    /// <summary>
    /// Asks the user for the credentials of an HTTP proxy: it receives the realm and the credentials entered so far
    /// ("user:password", or null on the first attempt) and returns the new credentials, or null if the user cancelled.
    /// The WPF window is injected here (by <c>App</c>) because this controller is compiled by the Mono test harness,
    /// which cannot reference the WPF dialog.
    /// </summary>
    public static Func<string, string, string> ProxyCredentialsPrompt;

    public event Action<bool> CloseRequested;

    public event PropertyChangedEventHandler PropertyChanged;

    public NewConnectionController(SSHConnectionSettings settings, WindowCustomization customization, Dispatcher dispatcher)
    {
        _Dispatcher = dispatcher;
        // Without it the connector creates its dialogs (the new/changed host key prompt, the proxy credentials prompt)
        // on its worker thread, and creating a WPF window off the STA UI thread throws "The calling thread must be STA".
        _BackgroundSessionInitiator.Dispatcher = dispatcher;
        _Settings = settings?.CloneSlow() ?? new SSHConnectionSettings();
        if (customization != null && customization.BypassHostKeyCheck)
        {
            _Settings.BypassHostKeyCheck = true;
        }
        _BackgroundSessionInitiator.StatusChanged += backgroundSessionInitiator1_ConnectionProgress;
        _BackgroundSessionInitiator.Connected += backgroundSessionInitiator1_SessionCreated;
        _BackgroundSessionInitiator.ProxyCredentialsRequested += backgroundSessionInitiator1_ProxyAuthRequest;
        Customization = customization;
        if (customization != null && customization.VMNameText != null)
        {
            HostNameCaption = "QEMU VM:";
        }
        else
        {
            HostNameCaption = "Host name:";
        }
        UseProxy = !string.IsNullOrEmpty(_Settings.ProxyServer);
        ProxyType = _Settings.ProxyType;
        // The field shows what is saved (it was left empty, so a connection with a proxy had to be typed in again to be
        // changed or even connected from this window), in the same "[user:password@]host:port" form it is read from.
        Proxy = SSHConnectionSettings.FormatProxy(_Settings.ProxyAuth, _Settings.ProxyServer, _Settings.ProxyPort);
        ConnectionAlias = _Settings.ConnectionAlias;
        HasAlias = !string.IsNullOrEmpty(_Settings.ConnectionAlias);
        HasCustomSSHKeyFile = !string.IsNullOrEmpty(_Settings.ExplicitKeyFile);
        CustomSSHKeyFile = _Settings.ExplicitKeyFile;
        // 0 is "not set" and means the default interval (see SSHConnectionSettings.KeepaliveInterval); only a negative value is "off".
        HasKeepaliveInterval = _Settings.KeepaliveInterval >= 0;
        KeepAliveInterval = _Settings.KeepaliveInterval > 0 ? _Settings.KeepaliveInterval : SSHConnectionSettings.DefaultKeepaliveSeconds;
        if (AuthenticationType == AuthType.Password)
        {
            try
            {
                Password = PasswordProtector.Decrypt(_Settings.EncryptedSavedPassword, "com.sysprogs.smartty.passwords");
            }
            catch
            {
            }
        }
    }

    public void BrowseForKeyFile()
    {
        string keyFile = ThemedFileDialog.OpenFile(null, "Select a private key file", "Private keys (id_*, *.ppk)|id_ed25519;id_ecdsa;id_rsa;*.ppk|All files (*.*)|*.*");
        if (keyFile != null)
        {
            CustomSSHKeyFile = keyFile;
        }
    }

    private void UpdateSettingsFieldsOrThrow()
    {
        if (UseProxy)
        {
            string text = Proxy;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new Exception("Please specify the proxy server address");
            }
            // "[user:password@]host:port", also with an IPv6 address in brackets. Without a valid port the proxy used to be
            // dropped silently and the connection went out directly, bypassing it; the credentials of an earlier entry
            // stayed in the settings when the new text had none.
            if (!SSHConnectionSettings.TryParseProxy(text, out SshProxyType? schemeType, out string proxyCredentials, out string proxyHost, out int proxyPort))
            {
                throw new Exception("Please specify the proxy server as host:port (for example proxy.example.com:3128, or 127.0.0.1:10808 for the SOCKS5 inbound of xray)");
            }
            if (schemeType != null)
            {
                ProxyType = schemeType.Value;
            }
            // The credentials are saved encrypted, and the encryption takes at most 200 bytes.
            if (proxyCredentials != null && System.Text.Encoding.UTF8.GetByteCount(proxyCredentials) > 200)
            {
                throw new Exception("The proxy user name and password are too long to be saved (200 bytes at most)");
            }
            _Settings.ProxyAuth = proxyCredentials;
            _Settings.ProxyServer = proxyHost;
            _Settings.ProxyPort = proxyPort;
            _Settings.ProxyType = ProxyType;
        }
        else
        {
            _Settings.ProxyAuth = null;
            _Settings.ProxyServer = null;
            _Settings.ProxyPort = 0;
            _Settings.ProxyType = SshProxyType.Http;
        }
        if (HasAlias)
        {
            _Settings.ConnectionAlias = _ConnectionAlias;
        }
        else
        {
            _Settings.ConnectionAlias = null;
        }
        if (HasCustomSSHKeyFile)
        {
            _Settings.ExplicitKeyFile = CustomSSHKeyFile;
        }
        else
        {
            _Settings.ExplicitKeyFile = null;
        }
        if (HasKeepaliveInterval)
        {
            _Settings.KeepaliveInterval = KeepAliveInterval > 0 ? KeepAliveInterval : SSHConnectionSettings.DefaultKeepaliveSeconds;
        }
        else
        {
            // An unchecked box must stay off: 0 would read as "not set" and bring the default back.
            _Settings.KeepaliveInterval = SSHConnectionSettings.KeepaliveOff;
        }
        if (AuthenticationType == AuthType.Password && Customization.AllowSavingPassword && !SetupPublicKey)
        {
            _Settings.EncryptedSavedPassword = PasswordProtector.Encrypt(Password, "com.sysprogs.smartty.passwords");
        }
    }

    /// <summary>True when the window was closed by "Save without connecting": there is no session, only <see cref="FinalSettings"/> to save.</summary>
    public bool SavedWithoutConnecting { get; private set; }

    /// <summary>Checks the entered host, user name and key file; the same checks for connecting and for saving.</summary>
    private void ValidateOrThrow()
    {
        if (string.IsNullOrEmpty(_Settings.HostName))
        {
            throw new Exception("The host name cannot be empty!");
        }
        if (_Settings.HostName?.ToLower() == "localhost")
        {
            throw new Exception("'localhost' is not a valid name for a remote Linux machine.\nIf you are using containers/WSL, consider using the IP address instead, or specifying the port explicitly (e.g. 'localhost:22')");
        }
        if (string.IsNullOrEmpty(_Settings.UserName))
        {
            throw new Exception("The user name cannot be empty!");
        }
        if (!string.IsNullOrEmpty(_Settings.ExplicitKeyFile) && !File.Exists(_Settings.ExplicitKeyFile))
        {
            throw new Exception("The specified OpenSSH key file does not exist!");
        }
    }

    /// <summary>Takes the entered settings without connecting; the caller saves <see cref="FinalSettings"/> when the window closes.</summary>
    public void SaveWithoutConnecting()
    {
        ValidateOrThrow();
        UpdateSettingsFieldsOrThrow();
        SavedWithoutConnecting = true;
        CloseRequested?.Invoke(true);
    }

    public void Connect()
    {
        ValidateOrThrow();
        UpdateSettingsFieldsOrThrow();
        ProgressText = "Connecting...";
        bool password = AuthenticationType == AuthType.Password;
        SshConnector backgroundSessionInitiator = _BackgroundSessionInitiator;
        SSHConnectionSettings settings = _Settings;
        string text;
        if (password)
        {
            text = Password;
        }
        else
        {
            text = (HasPassphrase ? Passphrase : null);
        }
        backgroundSessionInitiator.ConnectAsync(settings, text, SetupPublicKey & password);
    }

    private void DoReportException(Exception exception)
    {
        _Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            ReportException?.Invoke(exception);
        }), new object[0]);
    }

    private void backgroundSessionInitiator1_ConnectionProgress(SshConnector sender, string text, Exception exception)
    {
        try
        {
            ProgressText = text;
            if (exception != null)
            {
                ProgressText = null;
                DoReportException(exception);
            }
        }
        catch (Exception progressException)
        {
            DoReportException(progressException);
        }
    }

    private void backgroundSessionInitiator1_SessionCreated(SshConnector sender, SshSession connectedSession, SSHConnectionSettings settings)
    {
        try
        {
            CreatedSession = connectedSession;
            _Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                CloseRequested?.Invoke(true);
            }), new object[0]);
        }
        catch (Exception progressException)
        {
            DoReportException(progressException);
        }
    }

    private void backgroundSessionInitiator1_ProxyAuthRequest(SshConnector sender, string realm, ref string auth, ref bool handled)
    {
        try
        {
            string credentials = ProxyCredentialsPrompt?.Invoke(realm, auth);
            if (credentials != null)
            {
                auth = credentials;
                handled = true;
            }
        }
        catch (Exception progressException)
        {
            DoReportException(progressException);
        }
    }

    public void Cancel()
    {
        if (ProgressText == null)
        {
            CloseRequested?.Invoke(false);
            return;
        }
        _BackgroundSessionInitiator.CancelConnection();
        ProgressText = null;
    }
}
