using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class ProxyAuthForm : DpiAwareForm
{

    private ProxyAuthForm(string text, string text2)
    {
        this.InitializeComponent();
        lblHostName.Text = text;
        if (text2 != null)
        {
            int num = text2.IndexOf(':');
            if (num == -1)
            {
                txtUserName.Text = text2;
                return;
            }
            txtUserName.Text = text2.Substring(0, num);
            txtPassword.Text = text2.Substring(num + 1);
        }
    }

    public static bool ShowPrompt(string text, ref string text2)
    {
        ProxyAuthForm proxyAuthForm = new ProxyAuthForm(text, text2);
        if (proxyAuthForm.ShowModal() != DialogResult.OK)
        {
            return false;
        }
        text2 = proxyAuthForm.txtUserName.Text + ":" + proxyAuthForm.txtPassword.Text;
        return true;
    }
}
