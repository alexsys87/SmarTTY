using Renci.SshNet.Common;
using SmarTTY.Controls;
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

namespace SmarTTY.Ssh;

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

    /// <summary>
    /// Asks whether to save the host key of a server connected to for the first time (settings, key fingerprint).
    /// The themed WPF window is injected here by <c>App</c>: this class is compiled by the Mono test harness, which
    /// cannot reference the WPF dialogs. Without a hook the key is not trusted.
    /// </summary>
    public static Func<SSHConnectionSettings, string, bool> NewHostKeyPrompt;

    /// <summary>
    /// Asks whether to keep connecting after the host key of a server changed (settings, the reason, or null).
    /// Without a hook the changed key is not accepted.
    /// </summary>
    public static Func<SSHConnectionSettings, string, bool> HostKeyMismatchPrompt;

    /// <summary>
    /// Tells the user that the list of known hosts is damaged or cannot be read, or that an accepted host key could not be saved:
    /// otherwise the hosts accepted so far would silently ask for confirmation again. Injected by <c>App</c> like the prompts above;
    /// without a hook nothing is shown.
    /// </summary>
    public static Action<string> KnownHostsProblemNotice;

    private static readonly object KnownHostsNoticeSync = new object();

    private static string _lastKnownHostsProblem;

    // A problem stays until somebody fixes the file, so it is shown once, not at every connection. A clean read of the list
    // makes the next problem news again.
    private void NoticeKnownHostsProblem(string problem, bool fromLoad)
    {
        lock (KnownHostsNoticeSync)
        {
            if (problem == null)
            {
                if (fromLoad)
                {
                    _lastKnownHostsProblem = null;
                }
                return;
            }
            if (problem == _lastKnownHostsProblem)
            {
                return;
            }
            _lastKnownHostsProblem = problem;
        }
        Action<string> notice = KnownHostsProblemNotice;
        if (notice != null)
        {
            InvokeOnUi(() => notice(problem));
        }
    }

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

    private void RaiseConnected(ConnectionAttempt connectionAttempt, SshSession sshSession, SSHConnectionSettings sshConnectionSettings)
    {
        if (IsOffUiThread)
        {
            BeginInvokeOnUi(() =>
            {
                RaiseConnected(connectionAttempt, sshSession, sshConnectionSettings);
            });
        }
        else
        {
            // The user may have cancelled after the worker had finished and before this ran: then nobody asked for this
            // session any more and it must not appear. Cancelling has already closed it (the attempt held it until now).
            if (!connectionAttempt.TryReleaseSession())
            {
                return;
            }
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
            confirmed = ThemedMessageBox.Show(message, appName, MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes;
        }
    }

    // A dialog of a cancelled attempt is not shown: this runs on the UI thread, the thread that cancels, so the check cannot
    // be overtaken by a cancel. "Not confirmed" is what every caller already treats as the user giving up.
    private bool Confirm(ConnectionAttempt connectionAttempt, PromptKind promptKind, string message)
    {
        bool confirmed = false;
        InvokeOnUi(() =>
        {
            if (connectionAttempt.Cancelled)
            {
                return;
            }
            AskConfirmation(promptKind, message, out confirmed);
        });
        return confirmed;
    }

    private void Authenticate(ConnectionAttempt connectionAttempt, SshSession session, SSHConnectionSettings sshConnectionSettings, string password, ref bool forcePasswordAuth)
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
                            if (Confirm(connectionAttempt, PromptKind.YesNoWarning, "Cannot authenticate using public key. Enter a password instead?"))
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
                                if (!connectionAttempt.Cancelled)
                                {
                                    CredentialsRequested(this, sshConnectionSettings, ref password, ref confirmed, isKeyPassphrase: true);
                                }
                            });
                            if (!confirmed)
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        else
                        {
                            if (!Confirm(connectionAttempt, PromptKind.YesNoWarning, "Cannot authenticate using OpenSSH key. Enter password instead?"))
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
                    // Nothing typed and nothing saved (or the saved password cannot be decrypted): ask first. Sending an empty
                    // password would be a failed login that the server logs and counts towards MaxAuthTries and fail2ban
                    // before the user has typed anything. An account without a password still works: the prompt returns "".
                    // Without a prompt handler there is nobody to ask, so the empty password is tried as before.
                    string attemptedPassword = password ?? (CredentialsRequested == null ? "" : null);
                    if (attemptedPassword != null && session.Authenticate(sshConnectionSettings.UserName, attemptedPassword))
                    {
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
                            if (!connectionAttempt.Cancelled)
                            {
                                CredentialsRequested(this, sshConnectionSettings, ref password, ref pwdConfirmed, isKeyPassphrase: false);
                            }
                        });
                        if (pwdConfirmed)
                        {
                            passwordConfirmed = true;
                            // A confirmed prompt is an answer even when it is empty; null would ask again forever.
                            password = password ?? "";
                            break;
                        }
                        throw new OperationCanceledException();
                    }
                    throw new SecurityException("Wrong password.");
            }
        }
    }

    private bool ConfirmNewHostKey(ConnectionAttempt connectionAttempt, SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo)
    {
        if (IsOffUiThread)
        {
            return (bool)InvokeOnUiWithResult((Func<bool>)(() => ConfirmNewHostKey(connectionAttempt, sshConnectionSettings, hostKeyInfo)));
        }
        if (connectionAttempt.Cancelled)
        {
            return false;
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
        return NewHostKeyPrompt != null && NewHostKeyPrompt(sshConnectionSettings, fingerprint);
    }

    private bool ConfirmHostKeyMismatch(ConnectionAttempt connectionAttempt, SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo, string mismatchReason)
    {
        if (sshConnectionSettings.SuppressKeyMismatchWarning)
        {
            return true;
        }
        if (IsOffUiThread)
        {
            return (bool)InvokeOnUiWithResult((Func<bool>)(() => ConfirmHostKeyMismatch(connectionAttempt, sshConnectionSettings, hostKeyInfo, mismatchReason)));
        }
        if (connectionAttempt.Cancelled)
        {
            return false;
        }
        return HostKeyMismatchPrompt != null && HostKeyMismatchPrompt(sshConnectionSettings, mismatchReason);
    }

    private void ConnectWorker(SSHConnectionSettings sshConnectionSettings, string password, ConnectionAttempt connectionAttempt, bool forcePasswordAuth, bool autoAcceptHostKey)
    {
        SshSession sshSession = null;
        try
        {
            // "host", "host:port", "[ipv6]" and "[ipv6]:port": a bare IPv6 address has colons of its own and no port.
            string hostName = SSHConnectionSettings.SplitHostPort(sshConnectionSettings.HostName, out int? parsedPort);
            int port = parsedPort ?? 22;
            ReportStatus(connectionAttempt, "Connecting...");
            KnownHostKeyStore knownHostKeyStore = new KnownHostKeyStore();
            NoticeKnownHostsProblem(knownHostKeyStore.LoadProblem, fromLoad: true);
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
                    sshConnectionParameters.ProxyType = sshConnectionSettings.ProxyType;
                    SplitProxyCredentials(proxyCreds, out sshConnectionParameters.ProxyUserName, out sshConnectionParameters.ProxyPassword);
                }
                try
                {
                    sshSession = new SshSession(sshConnectionParameters, connectionAttempt.CancelEvent);
                    // From here cancelling closes the session, which ends the key check and the login below.
                    connectionAttempt.SetSession(sshSession);
                    break;
                }
                catch (ProxyException exception) when (SshSession.IsProxyAuthenticationFailure(exception))
                {
                    bool proxyConfirmed = false;
                    InvokeOnUi(() =>
                    {
                        if (!connectionAttempt.Cancelled)
                        {
                            ProxyCredentialsRequested(this, sshConnectionSettings.ProxyServer, ref proxyCreds, ref proxyConfirmed);
                        }
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
                    if (!autoAcceptHostKey && !ConfirmNewHostKey(connectionAttempt, sshConnectionSettings, hostKeyInfo))
                    {
                        // The user did not trust the key: never go on to send credentials to an unverified host.
                        throw new OperationCanceledException();
                    }
                    knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                    NoticeKnownHostsProblem(knownHostKeyStore.Save(), fromLoad: false);
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
                        if (!ConfirmHostKeyMismatch(connectionAttempt, sshConnectionSettings, hostKeyInfo, mismatchReason))
                        {
                            // A changed host key may mean a man-in-the-middle attack: do not authenticate.
                            throw new OperationCanceledException();
                        }
                        knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                        NoticeKnownHostsProblem(knownHostKeyStore.Save(), fromLoad: false);
                    }
                }
            }
            // No credentials go to a server the user has stopped connecting to (a host key that needed no question gets here).
            connectionAttempt.ThrowIfCancelled();
            ReportStatus(connectionAttempt, "Authenticating...");
            Authenticate(connectionAttempt, sshSession, sshConnectionSettings, password, ref forcePasswordAuth);
            if (forcePasswordAuth)
            {
                ISshKeyPair sshKeyPair = AppEnvironment.KeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA));
                ReportStatus(connectionAttempt, "Configuring...");
                // The profile is switched to the key only when the server is known to accept it: otherwise the next
                // connection would ask for a password the profile no longer remembers to use.
                bool keyInstalled = false;
                try
                {
                    if (!AuthorizedKeysManager.IsKeyAuthorized(sshSession, sshKeyPair.GetPublicKeyBlob()))
                    {
                        AuthorizedKeysManager.AppendAuthorizedKey(sshSession, sshKeyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName), message => Confirm(connectionAttempt, PromptKind.YesNoWarning, message));
                    }
                    keyInstalled = true;
                }
                catch (Exception exception) when (!(exception is OperationCanceledException) && !sshSession.IsConnectionClosed)
                {
                    // A lost connection is thrown on (the usual reconnect handling follows); a refusal of the server
                    // (full flash, read-only home, no permission) leaves a working password connection.
                    if (!Confirm(connectionAttempt, PromptKind.YesNoWarning, "Cannot set up the public key: " + exception.Message + Environment.NewLine + "Continue connecting with the password?"))
                    {
                        throw new OperationCanceledException();
                    }
                }
                if (keyInstalled)
                {
                    sshConnectionSettings.AuthenticationType = AuthType.PublicKey;
                }
            }
            // Without periodic traffic a pulled cable is only noticed once the user types something, so a default is used
            // when none is set (0); a profile that switched the keep-alive off (negative) keeps it off.
            int keepAliveSeconds = sshConnectionSettings.KeepaliveInterval < 0 ? 0 : (sshConnectionSettings.KeepaliveInterval != 0 ? sshConnectionSettings.KeepaliveInterval : DefaultKeepAliveSeconds);
            sshSession.ConfigureKeepAlive(keepAliveSeconds);
            RaiseConnected(connectionAttempt, sshSession, sshConnectionSettings);
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
        // A background thread: a cancelled attempt is not waited for any more, and must not keep the process alive.
        Thread worker = new Thread(() =>
        {
            this.ConnectWorker(sshConnectionSettings, password, connectionAttempt, forcePasswordAuth, autoAcceptHostKey: false);
        })
        {
            IsBackground = true,
            Name = "SSH connect worker"
        };
        worker.Start();
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

    public void CancelConnection()
    {
        if (_currentAttempt != null)
        {
            _currentAttempt.Dispose();
            _currentAttempt = null;
        }
    }
}
