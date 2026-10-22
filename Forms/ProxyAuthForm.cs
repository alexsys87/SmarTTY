using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class ProxyAuthForm : DpiAwareForm
{

    private ProxyAuthForm(string hostName, string credentials)
    {
        this.InitializeComponent();
        FormIcons.Apply(this, "proxy");
        lblHostName.Text = hostName;
        if (credentials != null)
        {
            int colonIdx = credentials.IndexOf(':');
            if (colonIdx == -1)
            {
                txtUserName.Text = credentials;
                return;
            }
            txtUserName.Text = credentials.Substring(0, colonIdx);
            txtPassword.Text = credentials.Substring(colonIdx + 1);
        }
    }

    public static bool ShowPrompt(string hostName, ref string credentials)
    {
        ProxyAuthForm proxyAuthForm = new ProxyAuthForm(hostName, credentials);
        if (proxyAuthForm.ShowModal() != DialogResult.OK)
        {
            return false;
        }
        credentials = proxyAuthForm.txtUserName.Text + ":" + proxyAuthForm.txtPassword.Text;
        return true;
    }
}
