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
    /// Asks the user for the prompts of a keyboard-interactive login that the password does not answer, such as a
    /// verification code (settings, the name and instruction of the server, the prompts): returns the responses in the
    /// order of the prompts, or null when the user cancelled. Injected by <c>App</c> like the host key prompts. Without a
    /// hook those prompts are answered with an empty text.
    /// </summary>
    public static Func<SSHConnectionSettings, string, string, SshSession.KeyboardInteractivePrompt[], string[]> KeyboardInteractivePrompt;

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

    // A failure of the new connection that comes from the cancelling of the attempt is a cancel, not a rejected key.
    private static string CheckKeyLogin(ConnectionAttempt connectionAttempt, SshSession sshSession, ISshKeyPair sshKeyPair)
    {
        string problem = sshSession.CheckPublicKeyLogin(sshKeyPair, connectionAttempt.CancelEvent);
        connectionAttempt.ThrowIfCancelled();
        return problem;
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
                    string attemptedPassword = ConnectionRules.FirstPasswordToTry(password, canAskUser: CredentialsRequested != null);
                    // A server with two-factor authentication asks for more than the password in the same login: the
                    // user answers those prompts (the verification code) here, the password answers its own prompt.
                    bool promptDeclined = false;
                    SshSession.KeyboardInteractiveHandler promptsForUser = KeyboardInteractivePrompt == null ? null : (string name, string instruction, SshSession.KeyboardInteractivePrompt[] prompts, string[] responses) =>
                    {
                        string[] answers = null;
                        InvokeOnUi(() =>
                        {
                            if (!connectionAttempt.Cancelled)
                            {
                                answers = KeyboardInteractivePrompt(sshConnectionSettings, name, instruction, prompts);
                            }
                        });
                        if (answers == null)
                        {
                            promptDeclined = true;
                            return;
                        }
                        Array.Copy(answers, responses, Math.Min(answers.Length, responses.Length));
                    };
                    bool passwordAccepted = attemptedPassword != null && session.Authenticate(sshConnectionSettings.UserName, attemptedPassword, promptsForUser);
                    if (promptDeclined)
                    {
                        // The user closed the question of the server: that is giving up, not a wrong password.
                        throw new OperationCanceledException();
                    }
                    if (passwordAccepted)
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
            fingerprint = HostKeyFingerprint.Describe(hostKeyInfo?.KeyData);
        }
        catch
        {
        }
        return NewHostKeyPrompt != null && NewHostKeyPrompt(sshConnectionSettings, fingerprint);
    }

    private bool ConfirmHostKeyMismatch(ConnectionAttempt connectionAttempt, SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo, string mismatchReason)
    {
        // Always asked: a changed host key may be a man-in-the-middle attack, so no setting of a profile may accept it
        // silently (an old profile that still has the "SuppressKeyMismatchWarning" element is read and the element dropped).
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
                    if (ConnectionRules.CompareHostKey(knownHostKey.KeyType, knownHostKey.KeyBase64, hostKeyInfo.Algorithm, hostKeyInfo.KeyData, out string mismatchReason) == ConnectionRules.HostKeyVerdict.Changed)
                    {
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
                    // The key line is not enough to go on: a fresh connection has to sign in with the key (the server may
                    // ignore the file, refuse the key type or the permissions of the home directory).
                    ReportStatus(connectionAttempt, "Checking key login...");
                    AuthorizedKeysManager.InstallKeyAndVerifyLogin(
                        sshSession,
                        sshKeyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName),
                        () => CheckKeyLogin(connectionAttempt, sshSession, sshKeyPair),
                        message => Confirm(connectionAttempt, PromptKind.YesNoWarning, message));
                    connectionAttempt.ThrowIfCancelled();
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
            int keepAliveSeconds = ConnectionRules.KeepAliveSeconds(sshConnectionSettings.KeepaliveInterval, DefaultKeepAliveSeconds);
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

    private static string NormalizeKeyType(string keyType)
    {
        return ConnectionRules.NormalizeKeyType(keyType);
    }

    private static void SplitProxyCredentials(string credentials, out string userName, out string password)
    {
        ConnectionRules.SplitProxyCredentials(credentials, out userName, out password);
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
