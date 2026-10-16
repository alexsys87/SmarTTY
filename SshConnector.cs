using ManagedLibSSH2;
using SmarTTY.Forms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

        private TcpClient _tcpClient;

        public bool Cancelled;

        public EventWaitHandle CancelEvent = new ManualResetEvent(initialState: false);

        public Socket GetSocket()
        {
            if (_tcpClient != null)
            {
                return _tcpClient.Client;
            }
            return null;
        }

        public void Connect(IPAddress[] items, int value2)
        {
            if (items == null || items.Length == 0)
            {
                throw new Exception("Cannot connect to a host with no IP address");
            }
            if (_tcpClient != null)
            {
                _tcpClient.Close();
            }
            _tcpClient = new TcpClient(items[0].AddressFamily);
            _tcpClient.Connect(items, value2);
        }

        public void Dispose()
        {
            Cancelled = true;
            if (CancelEvent != null)
            {
                CancelEvent.Set();
            }
            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient = null;
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

        public void DetachClient()
        {
            _tcpClient = null;
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
            _currentAttempt?.DetachClient();
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

    private void Authenticate(LibSsh2Session libSsh2Session, SSHConnectionSettings sshConnectionSettings, string text, ref bool flag4)
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
                        bool flag3 = libSsh2Session.AuthenticateWithPublicKey(sshConnectionSettings.UserName, sshKeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA)));
                        if ((sshConnectionSettings.KeyType == PublicKeyType.Auto) & flag3)
                        {
                            sshConnectionSettings.KeyType = PublicKeyType.RSA;
                        }
                        if (!flag3 && sshConnectionSettings.KeyType == PublicKeyType.Auto)
                        {
                            flag3 = libSsh2Session.AuthenticateWithPublicKey(sshConnectionSettings.UserName, sshKeyFactory.CreateKey(PublicKeyType.DSA));
                            if (flag3)
                            {
                                sshConnectionSettings.KeyType = PublicKeyType.DSA;
                            }
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
                        if (libSsh2Session.AuthenticateWithKeyFile(sshConnectionSettings.UserName, out flag2, sshConnectionSettings.ExplicitKeyFile, text))
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
                    if (libSsh2Session.Authenticate(sshConnectionSettings.UserName, text ?? ""))
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

    private bool ConfirmNewHostKey(SSHConnectionSettings sshConnectionSettings, LibSsh2Session.HostKeyInfo hostKeyInfo)
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

    private bool ConfirmHostKeyMismatch(SSHConnectionSettings sshConnectionSettings, LibSsh2Session.HostKeyInfo hostKeyInfo, string text)
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

    private IPAddress[] ResolveHost(string text, ConnectionAttempt connectionAttempt)
    {
        if (IPAddress.TryParse(text, out var address))
        {
            return new IPAddress[1] { address };
        }
        ReportStatus(connectionAttempt, "Looking up host...");
        IAsyncResult asyncResult = Dns.BeginGetHostEntry(text, null, null);
        WaitHandle.WaitAny(new WaitHandle[2] { asyncResult.AsyncWaitHandle, connectionAttempt.CancelEvent });
        if (!asyncResult.IsCompleted)
        {
            throw new Exception("Cannot lookup host address");
        }
        return Dns.EndGetHostEntry(asyncResult).AddressList;
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
            if (string.IsNullOrEmpty(sshConnectionSettings.ProxyServer))
            {
                IPAddress[] array = ResolveHost(text, connectionAttempt);
                ReportStatus(connectionAttempt, "Connecting...");
                connectionAttempt.Connect(array, num);
            }
            else
            {
                IPAddress[] array2 = ResolveHost(sshConnectionSettings.ProxyServer, connectionAttempt);
                ReportStatus(connectionAttempt, "Connecting...");
                connectionAttempt.Connect(array2, sshConnectionSettings.ProxyPort);
                string text4 = sshConnectionSettings.ProxyAuth;
                string text5;
                while (!ProxyConnect(connectionAttempt, num, text, text4, out text5))
                {
                    ReportStatus(connectionAttempt, "Reconnecting...");
                    bool flag3 = false;
                    InvokeOnUi(() =>
                    {
                        ProxyCredentialsRequested(this, text5, ref text4, ref flag3);
                    });
                    if (flag3)
                    {
                        connectionAttempt.Connect(array2, sshConnectionSettings.ProxyPort);
                        continue;
                    }
                    throw new OperationCanceledException();
                }
            }
            ReportStatus(connectionAttempt, "Handshaking...");
            sshSession = new SshSession(connectionAttempt.GetSocket(), sshConnectionSettings.HostName, 22, sshConnectionSettings.EnableCompression, sshConnectionSettings.EnableOnTheFlyTAR);
            ReportStatus(connectionAttempt, "Checking key...");
            LibSsh2Session.HostKeyInfo hostKeyInfo = sshSession.GetHostKey();
            if (!sshConnectionSettings.BypassHostKeyCheck)
            {
                KnownHostKeyStore knownHostKeyStore = new KnownHostKeyStore();
                KnownHostKey knownHostKey = knownHostKeyStore.Find(sshConnectionSettings.HostName);
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
                    if (knownHostKey.KeyType != hostKeyInfo.Algorithm || !PasswordProtector.BytesEqual(array3, hostKeyInfo.KeyData))
                    {
                        string text2 = null;
                        if (knownHostKey.KeyType != hostKeyInfo.Algorithm)
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

    private bool ProxyConnect(ConnectionAttempt connectionAttempt, int value2, string text4, string text5, out string text6)
    {
        ReportStatus(connectionAttempt, "HTTP CONNECT...");
        text5 = (string.IsNullOrEmpty(text5) ? "" : $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(text5))}\r\n");
        string text = string.Format("CONNECT {0}:{1} HTTP/1.1\r\nHost: {0}:{1}\r\n{2}\r\n", text4, value2, text5);
        connectionAttempt.GetSocket().Send(Encoding.UTF8.GetBytes(text));
        List<byte> list = new List<byte>();
        byte[] array = new byte[1];
        byte[] array2 = new byte[4] { 13, 10, 13, 10 };
        int num = 0;
        while (connectionAttempt.GetSocket().Receive(array) >= 1)
        {
            list.Add(array[0]);
            if (array[0] == array2[num])
            {
                num++;
                if (num >= array2.Length)
                {
                    break;
                }
            }
            else
            {
                num = 0;
            }
        }
        string[] array3 = Encoding.UTF8.GetString(list.ToArray()).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (array3.Length < 1)
        {
            throw new Exception("Unrecognized reply from proxy");
        }
        text6 = null;
        string[] array4 = array3;
        foreach (string text2 in array4)
        {
            string text3 = "Proxy-Authenticate:".ToLower();
            if (text2.ToLower().StartsWith(text3))
            {
                string input = text2.Substring(text3.Length).Trim();
                Match match = new Regex("realm[ \t]*=[ \t]*\"([^\"]+)\"", RegexOptions.IgnoreCase).Match(input);
                if (match.Success)
                {
                    text6 = match.Groups[1].ToString();
                }
            }
        }
        array3[0].IndexOf(' ');
        Match match2 = new Regex("^HTTP/[0-9\\.]+ ([0-9]+) .*$").Match(array3[0]);
        if (!match2.Success)
        {
            throw new Exception(array3[0]);
        }
        int num3 = int.Parse(match2.Groups[1].ToString());
        if (num3 == 407)
        {
            return false;
        }
        if (num3 / 100 != 2)
        {
            throw new Exception(array3[0]);
        }
        return true;
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
