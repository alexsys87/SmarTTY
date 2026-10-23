using SmarTTY.Ssh;
using Renci.SshNet.Common;
using SmarTTY.Forms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY;

public partial class SshConnector : Component
{
    public enum PromptKind
    {
        YesNoWarning
    }

    public delegate void ConnectionStatusHandler(SshConnector sshConnector, string statusMessage, Exception exception);

    public delegate void ConnectionEstablishedHandler(SshConnector sshConnector, SshSession sshSession, SSHConnectionSettings sshConnectionSettings);

    public delegate void CredentialsRequestHandler(SshConnector sshConnector, SSHConnectionSettings sshConnectionSettings, ref string password, ref bool confirmed, bool isKeyPassphrase);

    public delegate bool ConfirmationHandler(SshConnector sshConnector, PromptKind promptKind, string message);

    public delegate void ProxyCredentialsHandler(SshConnector sshConnector, string serverHost, ref string credentials, ref bool confirmed);

    private class ConnectionAttempt : IDisposable
    {
        public Thread Thread;

        public bool Cancelled;

        public EventWaitHandle CancelEvent = new ManualResetEvent(initialState: false);

        public void Dispose()
        {
            Cancelled = true;
            if (CancelEvent != null)
            {
                CancelEvent.Set();
            }
            if (Thread != null)
            {
                if (!Thread.Join(500))
                {
                    Thread.Abort();
                    Thread.Join();
                }
                Thread = null;
            }
            if (CancelEvent != null)
            {
                CancelEvent.Close();
                CancelEvent = null;
            }
        }
    }

    // Used when the connection settings do not enable keep-alive packets, so that a dead link is detected without user input.
    private const int DefaultKeepAliveSeconds = 15;

    public Dispatcher Dispatcher;

    private ConnectionAttempt _currentAttempt;

    public ContainerControl SynchronizationControl { get; set; }

    public override ISite Site
    {
        get
        {
            return base.Site;
        }
        set
        {
            base.Site = value;
            if (value != null && value.GetService(typeof(IDesignerHost)) is IDesignerHost { RootComponent: var rootComponent } && rootComponent is ContainerControl)
            {
                SynchronizationControl = rootComponent as ContainerControl;
            }
        }
    }

    private bool IsOffUiThread
    {
        get
        {
            if (Dispatcher != null)
            {
                return Dispatcher.Thread != Thread.CurrentThread;
            }
            if (SynchronizationControl != null)
            {
                return SynchronizationControl.InvokeRequired;
            }
            return false;
        }
    }

    public event ConnectionStatusHandler StatusChanged;

    public event ConnectionEstablishedHandler Connected;

    public event CredentialsRequestHandler CredentialsRequested;

    public event ProxyCredentialsHandler ProxyCredentialsRequested;

    public event ConfirmationHandler ConfirmationRequested;

    public SshConnector()
        : this(null)
    {
    }

    public SshConnector(IContainer container)
    {
        container?.Add(this);
        InitializeComponent();
        Disposed += OnDisposed;
    }

    private void BeginInvokeOnUi(ThreadStart callback)
    {
        if (Dispatcher != null)
        {
            Dispatcher.BeginInvoke((Delegate)callback, new object[0]);
        }
        else
        {
            SynchronizationControl.BeginInvoke(callback);
        }
    }

    private void InvokeOnUi(ThreadStart callback)
    {
        if (!IsOffUiThread)
        {
            callback();
        }
        else if (Dispatcher != null)
        {
            Dispatcher.Invoke((Delegate)callback, new object[0]);
        }
        else
        {
            SynchronizationControl.Invoke(callback);
        }
    }

    private object InvokeOnUiWithResult(Delegate delegateValue, params object[] items)
    {
        if (!IsOffUiThread)
        {
            return delegateValue.DynamicInvoke(items);
        }
        if (Dispatcher != null)
        {
            return Dispatcher.Invoke(delegateValue, items);
        }
        return SynchronizationControl.Invoke(delegateValue, items);
    }

    private void OnDisposed(object sender, EventArgs e)
    {
        CancelConnection();
    }

    private void RaiseConnected(SshSession sshSession, SSHConnectionSettings sshConnectionSettings)
    {
        if (IsOffUiThread)
        {
            BeginInvokeOnUi(() =>
            {
                RaiseConnected(sshSession, sshConnectionSettings);
            });
        }
        else
        {
            sshConnectionSettings.UpdateLastConnectionTime();
            Connected?.Invoke(this, sshSession, sshConnectionSettings);
        }
    }

    private void ReportStatus(ConnectionAttempt connectionAttempt, string statusMessage, Exception exception = null)
    {
        if (IsOffUiThread)
        {
            BeginInvokeOnUi(() =>
            {
                ReportStatus(connectionAttempt, statusMessage, exception);
            });
        }
        else if (!connectionAttempt.Cancelled && StatusChanged != null)
        {
            StatusChanged(this, statusMessage, exception);
        }
    }

    private void AskConfirmation(PromptKind promptKind, string message, out bool confirmed)
    {
        string appName = Assembly.GetExecutingAssembly().GetName().Name;
        if (ConfirmationRequested != null)
        {
            confirmed = ConfirmationRequested(this, promptKind, message);
        }
        else
        {
            confirmed = MessageBox.Show(message, appName, MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes;
        }
    }

    private bool Confirm(PromptKind promptKind, string message)
    {
        bool confirmed = false;
        InvokeOnUi(() =>
        {
            AskConfirmation(promptKind, message, out confirmed);
        });
        return confirmed;
    }

    private void Authenticate(SshSession session, SSHConnectionSettings sshConnectionSettings, string password, ref bool forcePasswordAuth, System.Runtime.CompilerServices.StrongBox<string> acceptedPassword)
    {
        bool passwordConfirmed = false;
        while (true)
        {
            switch (sshConnectionSettings.AuthenticationType)
            {
                default:
                    return;
                case AuthType.PublicKey:
                    {
                        SshKeyFactory sshKeyFactory = AppEnvironment.KeyFactory;
                        bool authSuccess = session.AuthenticateWithPublicKey(sshConnectionSettings.UserName, sshKeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA)));
                        if ((sshConnectionSettings.KeyType == PublicKeyType.Auto) & authSuccess)
                        {
                            sshConnectionSettings.KeyType = PublicKeyType.RSA;
                        }
                        if (!authSuccess)
                        {
                            // Some devices (embedded Linux with a volatile or unsupported authorized_keys) do not keep the key. With a saved
                            // password there is nothing to ask: fall back to it and set the key up again, as after a "yes" below.
                            if (sshConnectionSettings.EncryptedSavedPassword != null || Confirm(PromptKind.YesNoWarning, "Cannot authenticate using public key. Enter a password instead?"))
                            {
                                sshConnectionSettings.AuthenticationType = AuthType.Password;
                                forcePasswordAuth = true;
                                break;
                            }
                            throw new OperationCanceledException();
                        }
                        return;
                    }
                case AuthType.OpenSSHKey:
                    {
                        bool isKeyPassphrase = false;
                        if (session.AuthenticateWithKeyFile(sshConnectionSettings.UserName, out isKeyPassphrase, sshConnectionSettings.ExplicitKeyFile, password))
                        {
                            return;
                        }
                        if (isKeyPassphrase)
                        {
                            if (CredentialsRequested == null)
                            {
                                throw new Exception("Passphrase required");
                            }
                            bool confirmed = false;
                            InvokeOnUi(() =>
                            {
                                CredentialsRequested(this, sshConnectionSettings, ref password, ref confirmed, isKeyPassphrase: true);
                            });
                            if (!confirmed)
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        else
                        {
                            if (!Confirm(PromptKind.YesNoWarning, "Cannot authenticate using OpenSSH key. Enter password instead?"))
                            {
                                throw new OperationCanceledException();
                            }
                            sshConnectionSettings.AuthenticationType = AuthType.Password;
                        }
                        break;
                    }
                case AuthType.Password:
                    if (password == null && sshConnectionSettings.EncryptedSavedPassword != null)
                    {
                        try
                        {
                            password = PasswordProtector.Decrypt(sshConnectionSettings.EncryptedSavedPassword, "com.sysprogs.smartty.passwords");
                        }
                        catch
                        {
                        }
                    }
                    if (session.Authenticate(sshConnectionSettings.UserName, password ?? ""))
                    {
                        acceptedPassword.Value = password;
                        if (passwordConfirmed && sshConnectionSettings.EncryptedSavedPassword != null && password != null)
                        {
                            sshConnectionSettings.EncryptedSavedPassword = PasswordProtector.Encrypt(password, "com.sysprogs.smartty.passwords");
                            sshConnectionSettings.Save();
                        }
                        return;
                    }
                    if (CredentialsRequested != null)
                    {
                        bool pwdConfirmed = false;
                        InvokeOnUi(() =>
                        {
                            CredentialsRequested(this, sshConnectionSettings, ref password, ref pwdConfirmed, isKeyPassphrase: false);
                        });
                        if (pwdConfirmed)
                        {
                            passwordConfirmed = true;
                            break;
                        }
                        throw new OperationCanceledException();
                    }
                    throw new SecurityException("Wrong password.");
            }
        }
    }

    private bool ConfirmNewHostKey(SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo)
    {
        if (IsOffUiThread)
        {
            return (bool)InvokeOnUiWithResult((Func<bool>)(() => ConfirmNewHostKey(sshConnectionSettings, hostKeyInfo)));
        }
        string fingerprint = null;
        try
        {
            if (hostKeyInfo != null && hostKeyInfo.KeyData != null)
            {
                byte[] hashBytes = new MD5CryptoServiceProvider().ComputeHash(hostKeyInfo.KeyData);
                if (hashBytes != null)
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    foreach (byte b in hashBytes)
                    {
                        stringBuilder.AppendFormat("{0:x2}:", b);
                    }
                    stringBuilder.Remove(stringBuilder.Length - 1, 1);
                    fingerprint = stringBuilder.ToString();
                }
            }
        }
        catch
        {
        }
        return NewHostKeyForm.Confirm(sshConnectionSettings, fingerprint);
    }

    private bool ConfirmHostKeyMismatch(SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo, string mismatchReason)
    {
        if (sshConnectionSettings.SuppressKeyMismatchWarning)
        {
            return true;
        }
        if (IsOffUiThread)
        {
            return (bool)InvokeOnUiWithResult((Func<bool>)(() => ConfirmHostKeyMismatch(sshConnectionSettings, hostKeyInfo, mismatchReason)));
        }
        return HostKeyMismatchForm.Confirm(sshConnectionSettings, mismatchReason);
    }

    private void ConnectWorker(SSHConnectionSettings sshConnectionSettings, string password, ConnectionAttempt connectionAttempt, bool forcePasswordAuth, bool autoAcceptHostKey)
    {
        SshSession sshSession = null;
        try
        {
            int port = 22;
            string hostName = sshConnectionSettings.HostName;
            int colonIdx = hostName.IndexOf(':');
            if (colonIdx != -1 && int.TryParse(hostName.Substring(colonIdx + 1), out var parsedPort))
            {
                port = parsedPort;
                hostName = hostName.Substring(0, colonIdx);
            }
            ReportStatus(connectionAttempt, "Connecting...");
            KnownHostKeyStore knownHostKeyStore = new KnownHostKeyStore();
            KnownHostKey knownHostKey = knownHostKeyStore.Find(sshConnectionSettings.HostName);
            SshConnectionParameters sshConnectionParameters = new SshConnectionParameters
            {
                Host = hostName,
                Port = port,
                DisplayHostName = sshConnectionSettings.HostName,
                UserName = sshConnectionSettings.UserName,
                EnableCompression = sshConnectionSettings.EnableCompression,
                EnableOnTheFlyTar = sshConnectionSettings.EnableOnTheFlyTAR,
                PreferredHostKeyType = NormalizeKeyType(knownHostKey?.KeyType)
            };
            string proxyCreds = sshConnectionSettings.ProxyAuth;
            while (true)
            {
                if (!string.IsNullOrEmpty(sshConnectionSettings.ProxyServer))
                {
                    sshConnectionParameters.ProxyHost = sshConnectionSettings.ProxyServer;
                    sshConnectionParameters.ProxyPort = sshConnectionSettings.ProxyPort;
                    SplitProxyCredentials(proxyCreds, out sshConnectionParameters.ProxyUserName, out sshConnectionParameters.ProxyPassword);
                }
                try
                {
                    sshSession = new SshSession(sshConnectionParameters, connectionAttempt.CancelEvent);
                    break;
                }
                catch (ProxyException exception) when (exception.Message.Contains("407"))
                {
                    bool proxyConfirmed = false;
                    InvokeOnUi(() =>
                    {
                        ProxyCredentialsRequested(this, sshConnectionSettings.ProxyServer, ref proxyCreds, ref proxyConfirmed);
                    });
                    if (!proxyConfirmed)
                    {
                        throw new OperationCanceledException();
                    }
                    ReportStatus(connectionAttempt, "Reconnecting...");
                }
            }
            ReportStatus(connectionAttempt, "Checking key...");
            SshSession.HostKeyInfo hostKeyInfo = sshSession.GetHostKey();
            if (!sshConnectionSettings.BypassHostKeyCheck)
            {
                if (knownHostKey == null)
                {
                    if (!autoAcceptHostKey && !ConfirmNewHostKey(sshConnectionSettings, hostKeyInfo))
                    {
                        // The user did not trust the key: never go on to send credentials to an unverified host.
                        throw new OperationCanceledException();
                    }
                    knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                    knownHostKeyStore.Save();
                }
                else
                {
                    byte[] knownKeyData = Convert.FromBase64String(knownHostKey.KeyBase64);
                    string oldKeyType = NormalizeKeyType(knownHostKey.KeyType);
                    if (oldKeyType != hostKeyInfo.Algorithm || !PasswordProtector.BytesEqual(knownKeyData, hostKeyInfo.KeyData))
                    {
                        string mismatchReason = null;
                        if (oldKeyType != hostKeyInfo.Algorithm)
                        {
                            mismatchReason = "Key type changed from " + knownHostKey.KeyType + " to " + hostKeyInfo.Algorithm;
                        }
                        if (!ConfirmHostKeyMismatch(sshConnectionSettings, hostKeyInfo, mismatchReason))
                        {
                            // A changed host key may mean a man-in-the-middle attack: do not authenticate.
                            throw new OperationCanceledException();
                        }
                        knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                        knownHostKeyStore.Save();
                    }
                }
            }
            ReportStatus(connectionAttempt, "Authenticating...");
            System.Runtime.CompilerServices.StrongBox<string> acceptedPassword = new System.Runtime.CompilerServices.StrongBox<string>();
            Authenticate(sshSession, sshConnectionSettings, password, ref forcePasswordAuth, acceptedPassword);
            if (forcePasswordAuth && sshConnectionSettings.AuthenticationType == AuthType.Password && AuthorizedKeysManager.IsHomeDirectoryWritableByOthers(sshSession))
            {
                // The server (StrictModes) would refuse authorized_keys in a home directory others can write to, e.g. a world-writable
                // /root on tmpfs: a key set up here could never be used, and every connection would fall back to the password.
                bool savePassword = sshConnectionSettings.EncryptedSavedPassword == null && acceptedPassword.Value != null && Confirm(PromptKind.YesNoWarning,
                    "Public key authentication cannot work on this device: its home directory can be written by other users, so the SSH server will refuse the key.\n\nSave the password for this connection instead?");
                if (savePassword)
                {
                    sshConnectionSettings.EncryptedSavedPassword = PasswordProtector.Encrypt(acceptedPassword.Value, "com.sysprogs.smartty.passwords");
                    sshConnectionSettings.Save();
                }
            }
            else if (forcePasswordAuth)
            {
                ISshKeyPair sshKeyPair = AppEnvironment.KeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA));
                ReportStatus(connectionAttempt, "Configuring...");
                if (!AuthorizedKeysManager.IsKeyAuthorized(sshSession, sshKeyPair.GetPublicKeyBlob()))
                {
                    AuthorizedKeysManager.AppendAuthorizedKey(sshSession, sshKeyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName));
                }
                sshConnectionSettings.AuthenticationType = AuthType.PublicKey;
            }
            // Without periodic traffic a pulled cable is only noticed once the user types something, so a default is used when none is set.
            sshSession.ConfigureKeepAlive(sshConnectionSettings.KeepaliveInterval != 0 ? sshConnectionSettings.KeepaliveInterval : DefaultKeepAliveSeconds);
            RaiseConnected(sshSession, sshConnectionSettings);
        }
        catch (Exception exception)
        {
            ReportStatus(connectionAttempt, "Error", exception);
            if (sshSession != null)
            {
                try
                {
                    sshSession.Dispose();
                    return;
                }
                catch
                {
                    return;
                }
            }
        }
    }

    // Older versions stored host key types unknown to them as libssh2 type numbers.
    private static string NormalizeKeyType(string keyType)
    {
        return keyType switch
        {
            "4" => "ecdsa-sha2-nistp384",
            "5" => "ecdsa-sha2-nistp521",
            "6" => "ssh-ed25519",
            _ => keyType,
        };
    }

    // Proxy credentials are stored as "user:password".
    private static void SplitProxyCredentials(string credentials, out string userName, out string password)
    {
        userName = null;
        password = null;
        if (string.IsNullOrEmpty(credentials))
        {
            return;
        }
        int colonIdx = credentials.IndexOf(':');
        if (colonIdx == -1)
        {
            userName = credentials;
            return;
        }
        userName = credentials.Substring(0, colonIdx);
        password = credentials.Substring(colonIdx + 1);
    }

    public void ConnectAsync(SSHConnectionSettings sshConnectionSettings, string password, bool forcePasswordAuth)
    {
        CancelConnection();
        ConnectionAttempt connectionAttempt = (_currentAttempt = new ConnectionAttempt());
        _currentAttempt.Thread = new Thread(() =>
        {
            this.ConnectWorker(sshConnectionSettings, password, connectionAttempt, forcePasswordAuth, autoAcceptHostKey: false);
        });
        _currentAttempt.Thread.Start();
    }

    public SshSession Connect(SSHConnectionSettings sshConnectionSettings, string password, bool forcePasswordAuth, bool autoAcceptHostKey)
    {
        CancelConnection();
        ConnectionAttempt connectionAttempt = (this._currentAttempt = new ConnectionAttempt());
        SshSession resultSession = null;
        ConnectionEstablishedHandler callback = (SshConnector sshConnector, SshSession sshSession, SSHConnectionSettings connSettings) =>
        {
            resultSession = sshSession;
        };
        try
        {
            Connected += callback;
            this.ConnectWorker(sshConnectionSettings, password, connectionAttempt, forcePasswordAuth, autoAcceptHostKey);
        }
        finally
        {
            Connected -= callback;
        }
        return resultSession;
    }

    private void IgnoreConnected(SshConnector sshConnector, SshSession sshSession, SSHConnectionSettings sshConnectionSettings)
    {
    }

    public void CancelConnection()
    {
        if (_currentAttempt != null)
        {
            _currentAttempt.Dispose();
            _currentAttempt = null;
        }
    }
}
