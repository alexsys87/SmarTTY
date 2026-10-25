using SmarTTY.Ssh;
using SmarTTY.Controls;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class PublicKeyManagerForm : DpiAwareForm
{

    private SshSession _session;

    private string _authorizedKeyLine;

    private SSHConnectionSettings _settings;

    private ISshKeyPair _keyPair;

    public PublicKeyManagerForm(SshSession sshSession, SSHConnectionSettings sshConnectionSettings)
    {
        InitializeComponent();
        FormIcons.Apply(this, "public-key");
        txtHost.Text = sshSession.HostName;
        txtUser.Text = sshSession.UserName;
        _settings = sshConnectionSettings;
        _keyPair = AppEnvironment.KeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA));
        _session = sshSession;
        this.ShowKey();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshKeyStatus();
    }

    private void RefreshKeyStatus()
    {
        txtKeyStatus.Text = "verifying...";
        txtKeyStatus.ForeColor = Color.Black;
        Button button = btnAccept;
        bool enabled = (btnStopAccepting.Enabled = false);
        button.Enabled = enabled;
        new Thread(CheckKeyStatus).Start(false);
    }

    private void CheckKeyStatus(object updateSettingsObj)
    {
        bool updateSettings = (bool)updateSettingsObj;
        bool isAuthorized = false;
        try
        {
            isAuthorized = AuthorizedKeysManager.IsKeyAuthorized(_session, _keyPair.GetPublicKeyBlob());
        }
        catch
        {
        }
        BeginInvoke((ThreadStart)(() =>
        {
            if (isAuthorized)
            {
                txtKeyStatus.Text = "accepted";
                txtKeyStatus.ForeColor = Color.DarkGreen;
                picRemoteSettings.Image = Icons.Load("key-unlocked");
                if (updateSettings && _settings != null)
                {
                    _settings.AuthenticationType = AuthType.PublicKey;
                }
            }
            else
            {
                txtKeyStatus.Text = "not accepted";
                txtKeyStatus.ForeColor = Color.Red;
                picRemoteSettings.Image = Icons.Load("key-locked");
                if (updateSettings && _settings != null)
                {
                    _settings.AuthenticationType = AuthType.Password;
                }
            }
            btnAccept.Enabled = !isAuthorized;
            btnStopAccepting.Enabled = isAuthorized;
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

    private void ShowKey()
    {
        lblKeyContainer.Text = _keyPair.ContainerName;
        txtPublicKey.Text = (_authorizedKeyLine = _keyPair.ToAuthorizedKeysLine(Environment.UserName + "@" + Environment.MachineName));
    }

    private void btnExportKey_Click(object sender, EventArgs e)
    {
        if (MessageBox.Show("If the exported key gets compromised, anyone who has access to it will be able to login to the servers selected for public key authentication. Continue exporting?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            if (_saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                using (FileStream fileStream = File.Create(_saveFileDialog.FileName))
                {
                    _keyPair.ExportPrivateKey(fileStream);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void btnImportKey_Click(object sender, EventArgs e)
    {
        if (MessageBox.Show("After you change the key you will need a password to login to servers selected for public key authentication. Continue importing?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            if (_openFileDialog.ShowDialog() == DialogResult.OK)
            {
                using (FileStream fileStream = File.OpenRead(_openFileDialog.FileName))
                {
                    _keyPair.ImportPrivateKey(fileStream);
                }
                this.ShowKey();
                RefreshKeyStatus();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void btnResetKey_Click(object sender, EventArgs e)
    {
        if (MessageBox.Show("After you change the key you will need a password to login to servers selected for public key authentication. Continue reseting the key?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) != DialogResult.Yes)
        {
            return;
        }
        try
        {
            _keyPair.ResetContainer();
            this.ShowKey();
            RefreshKeyStatus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
        }
    }

    private void btnAccept_Click(object sender, EventArgs e)
    {
        txtKeyStatus.Text = "updating...";
        txtKeyStatus.ForeColor = Color.Black;
        Button button = btnAccept;
        bool enabled = (btnStopAccepting.Enabled = false);
        button.Enabled = enabled;
        new Thread(AcceptKey).Start();
    }

    private void btnStopAccepting_Click(object sender, EventArgs e)
    {
        txtKeyStatus.Text = "updating...";
        txtKeyStatus.ForeColor = Color.Black;
        Button button = btnAccept;
        bool enabled = (btnStopAccepting.Enabled = false);
        button.Enabled = enabled;
        new Thread(StopAcceptingKey).Start();
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
        CheckKeyStatus(true);
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
        CheckKeyStatus(true);
        ReportFailure("Cannot stop accepting the key", error);
    }

    // Called on the worker thread: the answer of the user is awaited on the UI thread.
    private bool ConfirmCleanup(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return false;
        }
        try
        {
            return (bool)Invoke((Func<bool>)(() => MessageBox.Show(this, message, "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes));
        }
        catch (InvalidOperationException)
        {
            // The form was closed meanwhile.
            return false;
        }
    }

    // The status field only says "not accepted"; the reason (full flash, no permission, lost connection) is shown here.
    // Called from the worker thread after CheckKeyStatus, so the message follows the status update.
    private void ReportFailure(string title, Exception error)
    {
        if (error == null || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke((ThreadStart)(() =>
            {
                MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }));
        }
        catch (InvalidOperationException)
        {
            // The form was closed meanwhile.
        }
    }

    private void picRemoteSettings_Click(object sender, EventArgs e)
    {
        if (btnAccept.Enabled)
        {
            btnAccept_Click(this, EventArgs.Empty);
        }
        else if (btnStopAccepting.Enabled)
        {
            btnStopAccepting_Click(this, EventArgs.Empty);
        }
    }
}
