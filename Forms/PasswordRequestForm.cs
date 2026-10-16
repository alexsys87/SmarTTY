using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class PasswordRequestForm : DpiAwareForm
{
    private SSHConnectionSettings _sshConnectionSettings;

    private PasswordRequestForm(SSHConnectionSettings sshConnectionSettings, string text, bool flag)
    {
        this.InitializeComponent();
        _sshConnectionSettings = sshConnectionSettings;
        if (flag)
        {
            lblHeading.Text = "Passphrase required for your keyfile";
            lblPasswordDesc.Text = "Passphrase:";
            Text = "Enter SSH Passphrase";
        }
        else if (text != null)
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

    public static bool ShowPrompt(SSHConnectionSettings sshConnectionSettings, ref string text, bool flag, bool flag2)
    {
        PasswordRequestForm passwordRequestForm = new PasswordRequestForm(sshConnectionSettings, text, flag);
        passwordRequestForm.TopMost = flag2;
        if (passwordRequestForm.ShowModal() != DialogResult.OK)
        {
            return false;
        }
        text = passwordRequestForm.txtPassword.Text;
        return true;
    }

    [DllImport("User32")]
    private static extern int SetForegroundWindow(IntPtr handle);

    private void OnUnusedEvent(object sender, EventArgs e)
    {
    }
}
