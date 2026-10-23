using System.Windows;
using System.Windows.Markup;

namespace SmarTTY.GUI;

public partial class QuickConnectionWindow : Window, IComponentConnector
{
    public QuickConnectionWindow()
        : this(telnet: false)
    {
    }

    /// <summary>
    /// The same window serves "Quick SSH connection" (username@hostname and a password) and
    /// "Quick telnet connection" (hostname, hostname:port, or with a login in front: user:password@hostname:port).
    /// </summary>
    public QuickConnectionWindow(bool telnet)
    {
        InitializeComponent();
        if (telnet)
        {
            Title = "Quick Telnet Connection";
            lblHeader.Text = "Connect to a machine via telnet without remembering settings:";
            lblPassword.Visibility = Visibility.Collapsed;
            txtPassword.Visibility = Visibility.Collapsed;
            // The hint shown while the box is empty (see the TextBox style).
            txtMachine.Tag = "[user[:password]@]hostname[:port], telnets://hostname";
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }
}
