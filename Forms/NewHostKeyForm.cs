using System.Windows.Forms;
using VisualTTY;

namespace SmarTTY.Forms;

public partial class NewHostKeyForm : DpiAwareForm
{
    private SSHConnectionSettings _sshConnectionSettings;

    private NewHostKeyForm(SSHConnectionSettings sshConnectionSettings, string text)
    {
        InitializeComponent();
        FormIcons.Apply(this, "host-key-new");
        _sshConnectionSettings = sshConnectionSettings;
        this.lblHostName.Text = sshConnectionSettings.HostName;
        lblKey.Text = text;
    }

    public static bool Confirm(SSHConnectionSettings sshConnectionSettings, string text)
    {
        if (new NewHostKeyForm(sshConnectionSettings, text).ShowModal() != DialogResult.OK)
        {
            return false;
        }
        return true;
    }
}
