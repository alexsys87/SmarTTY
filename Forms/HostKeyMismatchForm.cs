using System;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class HostKeyMismatchForm : DpiAwareForm
{
    private SSHConnectionSettings _sshConnectionSettings;

    private HostKeyMismatchForm(SSHConnectionSettings sshConnectionSettings)
    {
        InitializeComponent();
        FormIcons.Apply(this, "host-key-warning");
        _sshConnectionSettings = sshConnectionSettings;
        lblHostName.Text = sshConnectionSettings.HostName;
    }

    public static bool Confirm(SSHConnectionSettings sshConnectionSettings, string text)
    {
        HostKeyMismatchForm hostKeyMismatchForm = new HostKeyMismatchForm(sshConnectionSettings);
        if (text != null)
        {
            hostKeyMismatchForm.lblTypeChange.Text = text;
            hostKeyMismatchForm.lblMainDesc.Text = "Host key type changed";
            hostKeyMismatchForm.lblDetailedDesc.Text = "SmarTTY has detected that the type of the public key of the server has changed. This could be caused by upgrading the server, or the SSH client library, but might also indicate that someone is eavesdropping on you.";
        }
        if (hostKeyMismatchForm.ShowModal() != DialogResult.OK)
        {
            return false;
        }
        return true;
    }

    private void btnContinue_Click(object sender, EventArgs e)
    {
        if (MessageBox.Show("Are you sure? If the remote computer has been compromised, the attacked might be able to capture your password!", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
        {
            DialogResult = DialogResult.OK;
        }
    }
}
