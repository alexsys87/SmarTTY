using ManagedLibSSH2;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using VisualTTY;
using VisualTTY.Properties;

namespace SmarTTY.Forms;

public partial class PublicKeyManagerForm : DpiAwareForm
{

    private LibSsh2Session _session;

    private string _authorizedKeyLine;

    private SSHConnectionSettings _settings;

    private ISshKeyPair _keyPair;

    public PublicKeyManagerForm(LibSsh2Session libSsh2Session, SSHConnectionSettings sshConnectionSettings)
    {
        InitializeComponent();
        txtHost.Text = libSsh2Session.HostName;
        txtUser.Text = libSsh2Session.UserName;
        _settings = sshConnectionSettings;
        _keyPair = AppEnvironment.KeyFactory.CreateKey(sshConnectionSettings.KeyType.ResolveAuto(PublicKeyType.RSA));
        _session = libSsh2Session;
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

    private void CheckKeyStatus(object obj)
    {
        bool flag = (bool)obj;
        bool flag2 = false;
        try
        {
            flag2 = AuthorizedKeysManager.IsKeyAuthorized(_session, _keyPair.GetPublicKeyBlob());
        }
        catch
        {
        }
        BeginInvoke((ThreadStart)(() =>
        {
            if (flag2)
            {
                txtKeyStatus.Text = "accepted";
                txtKeyStatus.ForeColor = Color.DarkGreen;
                picRemoteSettings.Image = Resources.unlocked;
                if (flag && _settings != null)
                {
                    _settings.AuthenticationType = AuthType.PublicKey;
                }
            }
            else
            {
                txtKeyStatus.Text = "not accepted";
                txtKeyStatus.ForeColor = Color.Red;
                picRemoteSettings.Image = Resources.locked;
                if (flag && _settings != null)
                {
                    _settings.AuthenticationType = AuthType.Password;
                }
            }
            btnAccept.Enabled = !flag2;
            btnStopAccepting.Enabled = flag2;
            if (flag && _settings != null)
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
        try
        {
            AuthorizedKeysManager.AppendAuthorizedKey(_session, _authorizedKeyLine);
        }
        catch
        {
        }
        CheckKeyStatus(true);
    }

    private void StopAcceptingKey()
    {
        try
        {
            AuthorizedKeysManager.RemoveAuthorizedKey(_session, _keyPair.GetPublicKeyBlob());
        }
        catch
        {
        }
        CheckKeyStatus(true);
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
