using SmarTTY.Ssh;
using Microsoft.Win32;
using SmarTTY;
using SmarTTY.Forms;
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

    public bool UseConnectProxy { get; set; }

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

    public event Action<bool> CloseRequested;

    public event PropertyChangedEventHandler PropertyChanged;

    public NewConnectionController(SSHConnectionSettings settings, WindowCustomization customization, Dispatcher dispatcher)
    {
        _Dispatcher = dispatcher;
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
        UseConnectProxy = !string.IsNullOrEmpty(_Settings.ProxyServer);
        ConnectionAlias = _Settings.ConnectionAlias;
        HasAlias = !string.IsNullOrEmpty(_Settings.ConnectionAlias);
        HasCustomSSHKeyFile = !string.IsNullOrEmpty(_Settings.ExplicitKeyFile);
        CustomSSHKeyFile = _Settings.ExplicitKeyFile;
        HasKeepaliveInterval = _Settings.KeepaliveInterval > 0;
        KeepAliveInterval = _Settings.KeepaliveInterval;
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
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = "OpenSSH keys|id_dsa;id_rsa|All files|*.*"
        };
        if (((CommonDialog)openFileDialog).ShowDialog() == true)
        {
            CustomSSHKeyFile = ((FileDialog)openFileDialog).FileName;
        }
    }

    private void UpdateSettingsFieldsOrThrow()
    {
        if (UseConnectProxy)
        {
            string text = Proxy;
            if (text == null)
            {
                throw new Exception("Please specify the proxy server address");
            }
            int num = text.LastIndexOf('@');
            if (num != -1)
            {
                _Settings.ProxyAuth = text.Substring(0, num);
                text = text.Substring(num + 1);
            }
            num = text.IndexOf(':');
            if (num != -1 && int.TryParse(text.Substring(num + 1), out var result))
            {
                _Settings.ProxyServer = text.Substring(0, num);
                _Settings.ProxyPort = result;
            }
        }
        else
        {
            SSHConnectionSettings settings = _Settings;
            string proxyServer = (_Settings.ProxyAuth = null);
            settings.ProxyServer = proxyServer;
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
            _Settings.KeepaliveInterval = KeepAliveInterval;
        }
        else
        {
            _Settings.KeepaliveInterval = 0;
        }
        if (AuthenticationType == AuthType.Password && Customization.AllowSavingPassword && !SetupPublicKey)
        {
            _Settings.EncryptedSavedPassword = PasswordProtector.Encrypt(Password, "com.sysprogs.smartty.passwords");
        }
    }

    public void Connect()
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
        UpdateSettingsFieldsOrThrow();
        ProgressText = "Connecting...";
        bool flag = AuthenticationType == AuthType.Password;
        SshConnector backgroundSessionInitiator = _BackgroundSessionInitiator;
        SSHConnectionSettings settings = _Settings;
        string text;
        if (flag)
        {
            text = Password;
        }
        else
        {
            text = (HasPassphrase ? Passphrase : null);
        }
        backgroundSessionInitiator.ConnectAsync(settings, text, SetupPublicKey & flag);
    }

    private void DoReportException(Exception ex)
    {
        _Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            ReportException?.Invoke(ex);
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
        catch (Exception ex)
        {
            DoReportException(ex);
        }
    }

    private void backgroundSessionInitiator1_SessionCreated(SshConnector sender, SshSession connectedSession, SSHConnectionSettings settings)
    {
        try
        {
            CreatedSession = connectedSession;
            _Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                CloseRequested?.Invoke(obj: true);
            }), new object[0]);
        }
        catch (Exception ex)
        {
            DoReportException(ex);
        }
    }

    private void backgroundSessionInitiator1_ProxyAuthRequest(SshConnector sender, string realm, ref string auth, ref bool handled)
    {
        try
        {
            handled = ProxyAuthForm.ShowPrompt(realm, ref auth);
        }
        catch (Exception ex)
        {
            DoReportException(ex);
        }
    }

    public void Cancel()
    {
        if (ProgressText == null)
        {
            CloseRequested?.Invoke(obj: false);
            return;
        }
        _BackgroundSessionInitiator.CancelConnection();
        ProgressText = null;
    }
}
