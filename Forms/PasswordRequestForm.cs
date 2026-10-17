using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class PasswordRequestForm : DpiAwareForm
{
    private SSHConnectionSettings _sshConnectionSettings;

    private PasswordRequestForm(SSHConnectionSettings sshConnectionSettings, string previousPassword, bool isPassphrase)
    {
        this.InitializeComponent();
        _sshConnectionSettings = sshConnectionSettings;
        if (isPassphrase)
        {
            lblHeading.Text = "Passphrase required for your keyfile";
            lblPasswordDesc.Text = "Passphrase:";
            Text = "Enter SSH Passphrase";
        }
        else if (previousPassword != null)
        {
            lblHeading.Text = "Password not accepted";
        }
        lblHostName.Text = sshConnectionSettings.HostName;
        lblUserName.Text = sshConnectionSettings.UserName;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (TopMost)
        {
            SetForegroundWindow(Handle);
        }
    }

    public static bool ShowPrompt(SSHConnectionSettings sshConnectionSettings, ref string password, bool isPassphrase, bool isSudo)
    {
        PasswordRequestForm passwordRequestForm = new PasswordRequestForm(sshConnectionSettings, password, isPassphrase);
        passwordRequestForm.TopMost = isSudo;
        if (passwordRequestForm.ShowModal() != DialogResult.OK)
        {
            return false;
        }
        password = passwordRequestForm.txtPassword.Text;
        return true;
    }

    [DllImport("User32")]
    private static extern int SetForegroundWindow(IntPtr hWnd);

    private void OnUnusedEvent(object sender, EventArgs e)
    {
    }
}
