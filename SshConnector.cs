using ManagedLibSSH2;
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

    public delegate void ConnectionStatusHandler(SshConnector sshConnector, string text, Exception exception);

    public delegate void ConnectionEstablishedHandler(SshConnector sshConnector, SshSession sshSession, SSHConnectionSettings sshConnectionSettings);

    public delegate void CredentialsRequestHandler(SshConnector sshConnector, SSHConnectionSettings sshConnectionSettings, ref string text, ref bool flag, bool flag2);

    public delegate bool ConfirmationHandler(SshConnector sshConnector, PromptKind promptKind, string text);

    public delegate void ProxyCredentialsHandler(SshConnector sshConnector, string text, ref string text2, ref bool flag);

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

    private void ReportStatus(ConnectionAttempt connectionAttempt, string text, Exception exception = null)
    {
        if (IsOffUiThread)
        {
            BeginInvokeOnUi(() =>
            {
                ReportStatus(connectionAttempt, text, exception);
            });
        }
        else if (!connectionAttempt.Cancelled && StatusChanged != null)
        {
            StatusChanged(this, text, exception);
        }
    }

    private void AskConfirmation(PromptKind promptKind, string text, out bool flag)
    {
        string name = Assembly.GetExecutingAssembly().GetName().Name;
        if (ConfirmationRequested != null)
        {
            flag = ConfirmationRequested(this, promptKind, text);
        }
        else
        {
            flag = MessageBox.Show(text, name, MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes;
        }
    }

    private bool Confirm(PromptKind promptKind, string text)
    {
        bool flag = false;
        InvokeOnUi(() =>
        {
            AskConfirmation(promptKind, text, out flag);
        });
        return flag;
    }

    private void Authenticate(SshSession session, SSHConnectionSettings sshConnectionSettings, string text, ref bool flag4)
    {
        bool flag = false;
        while (true)
        {
            switch (sshConnectionSettings.AuthenticationType)
            {
                default:
                    return;
                case AuthType.PublicKey:
                    {
                        SshKeyFactory sshKeyFactory = AppEnvironment.KeyFactory;
                        bool flag3 = session.AuthenticateWithPublicKey(sshConnectionSettings.UserName, sshKeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA)));
                        if ((sshConnectionSettings.KeyType == PublicKeyType.Auto) & flag3)
                        {
                            sshConnectionSettings.KeyType = PublicKeyType.RSA;
                        }
                        if (!flag3)
                        {
                            if (Confirm(PromptKind.YesNoWarning, "Cannot authenticate using public key. Enter a password instead?"))
                            {
                                sshConnectionSettings.AuthenticationType = AuthType.Password;
                                flag4 = true;
                                break;
                            }
                            throw new OperationCanceledException();
                        }
                        return;
                    }
                case AuthType.OpenSSHKey:
                    {
                        bool flag2 = false;
                        if (session.AuthenticateWithKeyFile(sshConnectionSettings.UserName, out flag2, sshConnectionSettings.ExplicitKeyFile, text))
                        {
                            return;
                        }
                        if (flag2)
                        {
                            if (CredentialsRequested == null)
                            {
                                throw new Exception("Passphrase required");
                            }
                            bool flag5 = false;
                            InvokeOnUi(() =>
                            {
                                CredentialsRequested(this, sshConnectionSettings, ref text, ref flag5, flag2: true);
                            });
                            if (!flag5)
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
                    if (text == null && sshConnectionSettings.EncryptedSavedPassword != null)
                    {
                        try
                        {
                            text = PasswordProtector.Decrypt(sshConnectionSettings.EncryptedSavedPassword, "com.sysprogs.smartty.passwords");
                        }
                        catch
                        {
                        }
                    }
                    if (session.Authenticate(sshConnectionSettings.UserName, text ?? ""))
                    {
                        if (flag && sshConnectionSettings.EncryptedSavedPassword != null && text != null)
                        {
                            sshConnectionSettings.EncryptedSavedPassword = PasswordProtector.Encrypt(text, "com.sysprogs.smartty.passwords");
                            sshConnectionSettings.Save();
                        }
                        return;
                    }
                    if (CredentialsRequested != null)
                    {
                        bool flag6 = false;
                        InvokeOnUi(() =>
                        {
                            CredentialsRequested(this, sshConnectionSettings, ref text, ref flag6, flag2: false);
                        });
                        if (flag6)
                        {
                            flag = true;
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
        string text = null;
        try
        {
            if (hostKeyInfo != null && hostKeyInfo.KeyData != null)
            {
                byte[] array = new MD5CryptoServiceProvider().ComputeHash(hostKeyInfo.KeyData);
                if (array != null)
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    byte[] array2 = array;
                    foreach (byte b2 in array2)
                    {
                        stringBuilder.AppendFormat("{0:x2}:", b2);
                    }
                    stringBuilder.Remove(stringBuilder.Length - 1, 1);
                    text = stringBuilder.ToString();
                }
            }
        }
        catch
        {
        }
        return NewHostKeyForm.Confirm(sshConnectionSettings, text);
    }

    private bool ConfirmHostKeyMismatch(SSHConnectionSettings sshConnectionSettings, SshSession.HostKeyInfo hostKeyInfo, string text)
    {
        if (sshConnectionSettings.SuppressKeyMismatchWarning)
        {
            return true;
        }
        if (IsOffUiThread)
        {
            return (bool)InvokeOnUiWithResult((Func<bool>)(() => ConfirmHostKeyMismatch(sshConnectionSettings, hostKeyInfo, text)));
        }
        return HostKeyMismatchForm.Confirm(sshConnectionSettings, text);
    }

    private void ConnectWorker(SSHConnectionSettings sshConnectionSettings, string text3, ConnectionAttempt connectionAttempt, bool flag, bool flag2)
    {
        SshSession sshSession = null;
        try
        {
            int num = 22;
            string text = sshConnectionSettings.HostName;
            int num2 = text.IndexOf(':');
            if (num2 != -1 && int.TryParse(text.Substring(num2 + 1), out var result))
            {
                num = result;
                text = text.Substring(0, num2);
            }
            ReportStatus(connectionAttempt, "Connecting...");
            KnownHostKeyStore knownHostKeyStore = new KnownHostKeyStore();
            KnownHostKey knownHostKey = knownHostKeyStore.Find(sshConnectionSettings.HostName);
            SshConnectionParameters sshConnectionParameters = new SshConnectionParameters
            {
                Host = text,
                Port = num,
                DisplayHostName = sshConnectionSettings.HostName,
                UserName = sshConnectionSettings.UserName,
                EnableCompression = sshConnectionSettings.EnableCompression,
                EnableOnTheFlyTar = sshConnectionSettings.EnableOnTheFlyTAR,
                PreferredHostKeyType = NormalizeKeyType(knownHostKey?.KeyType)
            };
            string text4 = sshConnectionSettings.ProxyAuth;
            while (true)
            {
                if (!string.IsNullOrEmpty(sshConnectionSettings.ProxyServer))
                {
                    sshConnectionParameters.ProxyHost = sshConnectionSettings.ProxyServer;
                    sshConnectionParameters.ProxyPort = sshConnectionSettings.ProxyPort;
                    SplitProxyCredentials(text4, out sshConnectionParameters.ProxyUserName, out sshConnectionParameters.ProxyPassword);
                }
                try
                {
                    sshSession = new SshSession(sshConnectionParameters, connectionAttempt.CancelEvent);
                    break;
                }
                catch (ProxyException ex) when (ex.Message.Contains("407"))
                {
                    bool flag3 = false;
                    InvokeOnUi(() =>
                    {
                        ProxyCredentialsRequested(this, sshConnectionSettings.ProxyServer, ref text4, ref flag3);
                    });
                    if (!flag3)
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
                    if (flag2 || ConfirmNewHostKey(sshConnectionSettings, hostKeyInfo))
                    {
                        knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                        knownHostKeyStore.Save();
                    }
                }
                else
                {
                    byte[] array3 = Convert.FromBase64String(knownHostKey.KeyBase64);
                    string text5 = NormalizeKeyType(knownHostKey.KeyType);
                    if (text5 != hostKeyInfo.Algorithm || !PasswordProtector.BytesEqual(array3, hostKeyInfo.KeyData))
                    {
                        string text2 = null;
                        if (text5 != hostKeyInfo.Algorithm)
                        {
                            text2 = "Key type changed from " + knownHostKey.KeyType + " to " + hostKeyInfo.Algorithm;
                        }
                        if (ConfirmHostKeyMismatch(sshConnectionSettings, hostKeyInfo, text2))
                        {
                            knownHostKeyStore.Update(sshConnectionSettings.HostName, hostKeyInfo);
                            knownHostKeyStore.Save();
                        }
                    }
                }
            }
            ReportStatus(connectionAttempt, "Authenticating...");
            Authenticate(sshSession, sshConnectionSettings, text3, ref flag);
            if (flag)
            {
                ISshKeyPair sshKeyPair = AppEnvironment.KeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA));
                ReportStatus(connectionAttempt, "Configuring...");
                if (!AuthorizedKeysManager.IsKeyAuthorized(sshSession, sshKeyPair.GetPublicKeyBlob()))
                {
                    AuthorizedKeysManager.AppendAuthorizedKey(sshSession, sshKeyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName));
                }
                sshConnectionSettings.AuthenticationType = AuthType.PublicKey;
            }
            if (sshConnectionSettings.KeepaliveInterval != 0)
            {
                sshSession.ConfigureKeepAlive(sshConnectionSettings.KeepaliveInterval);
            }
            RaiseConnected(sshSession, sshConnectionSettings);
        }
        catch (Exception ex)
        {
            ReportStatus(connectionAttempt, "Error", ex);
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
    private static string NormalizeKeyType(string text)
    {
        return text switch
        {
            "4" => "ecdsa-sha2-nistp384",
            "5" => "ecdsa-sha2-nistp521",
            "6" => "ssh-ed25519",
            _ => text,
        };
    }

    // Proxy credentials are stored as "user:password".
    private static void SplitProxyCredentials(string text, out string userName, out string password)
    {
        userName = null;
        password = null;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        int num = text.IndexOf(':');
        if (num == -1)
        {
            userName = text;
            return;
        }
        userName = text.Substring(0, num);
        password = text.Substring(num + 1);
    }

    public void ConnectAsync(SSHConnectionSettings sshConnectionSettings, string text, bool flag)
    {
        CancelConnection();
        ConnectionAttempt connectionAttempt = (_currentAttempt = new ConnectionAttempt());
        _currentAttempt.Thread = new Thread(() =>
        {
            this.ConnectWorker(sshConnectionSettings, text, connectionAttempt, flag, flag2: false);
        });
        _currentAttempt.Thread.Start();
    }

    public SshSession Connect(SSHConnectionSettings sshConnectionSettings, string text, bool flag, bool flag2)
    {
        CancelConnection();
        ConnectionAttempt connectionAttempt = (this._currentAttempt = new ConnectionAttempt());
        SshSession sshSession2 = null;
        ConnectionEstablishedHandler callback = (SshConnector sshConnector, SshSession sshSession, SSHConnectionSettings sSHConnectionSettings) =>
        {
            sshSession2 = sshSession;
        };
        try
        {
            Connected += callback;
            this.ConnectWorker(sshConnectionSettings, text, connectionAttempt, flag, flag2);
        }
        finally
        {
            Connected -= callback;
        }
        return sshSession2;
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
