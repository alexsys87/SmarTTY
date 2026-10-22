using System.Diagnostics;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class AboutForm : DpiAwareForm
{
    public static string VersionTextOverride;

    public AboutForm()
    {
        InitializeComponent();
        FormIcons.Apply(this, "about");
        if (VersionTextOverride != null)
        {
            label2.Text = VersionTextOverride;
        }
    }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        Process.Start(linkLabel1.Text);
    }
}
