using Microsoft.Win32;
using SmarTTY.Ssh;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// The "Manage Public Key" dialog: shows the public key SmarTTY generated, lets it be exported, imported and reset, and
/// configures the remote account to accept it. The status of the remote side is checked on a worker thread.
/// </summary>
public partial class PublicKeyManagerWindow : Window
{
    private readonly SshSession _session;

    private readonly SSHConnectionSettings _settings;

    private readonly ISshKeyPair _keyPair;

    private string _authorizedKeyLine;


    private bool _closed;

    public PublicKeyManagerWindow(SshSession sshSession, SSHConnectionSettings settings)
    {
        InitializeComponent();
        _session = sshSession;
        _settings = settings;
        RemoteHostText.Text = sshSession.HostName;
        RemoteUserText.Text = sshSession.UserName;
        _keyPair = AppEnvironment.KeyFactory.CreateKey(settings.KeyType.ResolveAuto(PublicKeyType.RSA));
        ShowKey();
        Loaded += (object sender, RoutedEventArgs e) => RefreshKeyStatus();
        Closed += (object sender, EventArgs e) => _closed = true;
    }

    private void ShowKey()
    {
        KeyContainerText.Text = _keyPair.ContainerName;
        PublicKeyText.Text = (_authorizedKeyLine = _keyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName));
    }

    private void SetStatus(string text, string colorKey, bool authorized)
    {
        KeyStatusText.Text = text;
        KeyStatusText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        RemoteSettingsImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(authorized ? "pack://application:,,,/icons/keys/unlocked.png" : "pack://application:,,,/icons/keys/locked.png"));
        AcceptButton.IsEnabled = !authorized;
        StopAcceptingButton.IsEnabled = authorized;
    }

    private void RefreshKeyStatus()
    {
        KeyStatusText.Text = "verifying...";
        KeyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        AcceptButton.IsEnabled = false;
        StopAcceptingButton.IsEnabled = false;
        Task.Run(() => CheckKeyStatus(updateSettings: false));
    }

    // Runs on a worker thread; the result is applied on the UI thread.
    private void CheckKeyStatus(bool updateSettings)
    {
        bool isAuthorized = false;
        try
        {
            isAuthorized = AuthorizedKeysManager.IsKeyAuthorized(_session, _keyPair.GetPublicKeyBlob());
        }
        catch
        {
        }
        if (updateSettings && _settings != null)
        {
            _settings.AuthenticationType = isAuthorized ? AuthType.PublicKey : AuthType.Password;
        }
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_closed)
            {
                return;
            }
            SetStatus(
                isAuthorized ? "accepted" : "not accepted",
                isAuthorized ? "Theme.Permission.Executable" : "Theme.Error",
                isAuthorized);
            if (updateSettings && _settings != null)
            {
                try
                {
                    _settings.Save();
                }
                catch
                {
                }
            }
        }));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageBoxes.AskWarn("If the exported key gets compromised, anyone who has access to it will be able to login to the servers selected for public key authentication. Continue exporting?"))
        {
            return;
        }
        SaveFileDialog dialog = new SaveFileDialog { DefaultExt = "sshkey", Filter = "SSH keys|*.sshkey" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            using FileStream stream = File.Create(dialog.FileName);
            _keyPair.ExportPrivateKey(stream);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageBoxes.AskWarn("After you change the key you will need a password to login to servers selected for public key authentication. Continue importing?"))
        {
            return;
        }
        OpenFileDialog dialog = new OpenFileDialog { DefaultExt = "sshkey", Filter = "SSH keys|*.sshkey" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            using (FileStream stream = File.OpenRead(dialog.FileName))
            {
                _keyPair.ImportPrivateKey(stream);
            }
            ShowKey();
            RefreshKeyStatus();
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageBoxes.AskWarn("After you change the key you will need a password to login to servers selected for public key authentication. Continue reseting the key?"))
        {
            return;
        }
        try
        {
            _keyPair.ResetContainer();
            ShowKey();
            RefreshKeyStatus();
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        BeginRemoteChange();
        Task.Run(() => AcceptKey());
    }

    private void StopAccepting_Click(object sender, RoutedEventArgs e)
    {
        BeginRemoteChange();
        Task.Run(() => StopAcceptingKey());
    }

    private void BeginRemoteChange()
    {
        KeyStatusText.Text = "updating...";
        KeyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        AcceptButton.IsEnabled = false;
        StopAcceptingButton.IsEnabled = false;
    }

    private void AcceptKey()
    {
        Exception error = null;
        try
        {
            AuthorizedKeysManager.AppendAuthorizedKey(_session, _authorizedKeyLine, ConfirmCleanup);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        CheckKeyStatus(updateSettings: true);
        ReportFailure("Cannot accept the key", error);
    }

    private void StopAcceptingKey()
    {
        Exception error = null;
        try
        {
            AuthorizedKeysManager.RemoveAuthorizedKey(_session, _keyPair.GetPublicKeyBlob());
        }
        catch (Exception exception)
        {
            error = exception;
        }
        CheckKeyStatus(updateSettings: true);
        ReportFailure("Cannot stop accepting the key", error);
    }

    // Called on the worker thread: the answer of the user is awaited on the UI thread.
    private bool ConfirmCleanup(string message)
    {
        if (_closed)
        {
            return false;
        }
        try
        {
            return Dispatcher.Invoke(() => MessageBoxes.AskWarn(message));
        }
        catch (Exception)
        {
            // The window was closed meanwhile.
            return false;
        }
    }

    // The status field only says "not accepted"; the reason (full flash, no permission, lost connection) is shown here.
    // Called from the worker thread after CheckKeyStatus, so the message follows the status update.
    private void ReportFailure(string title, Exception error)
    {
        if (error == null || _closed)
        {
            return;
        }
        try
        {
            Dispatcher.BeginInvoke((Action)(() => MessageBoxes.Error(error.Message)));
        }
        catch (Exception)
        {
            // The window was closed meanwhile.
        }
    }

    private void RemoteSettings_Click(object sender, MouseButtonEventArgs e)
    {
        if (AcceptButton.IsEnabled)
        {
            Accept_Click(this, new RoutedEventArgs());
        }
        else if (StopAcceptingButton.IsEnabled)
        {
            StopAccepting_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Shows the dialog.</summary>
    internal static void Show(Window owner, SshSession session, SSHConnectionSettings settings)
    {
        PublicKeyManagerWindow window = new PublicKeyManagerWindow(session, settings);
        WindowOwnership.Attach(window, owner);
        window.ShowDialog();
    }
}
