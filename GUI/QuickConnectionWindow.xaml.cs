using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace SmarTTY.GUI;

public partial class QuickConnectionWindow : Window, IComponentConnector
{
    internal ResourceDictionary ThemeDictionary;

    public QuickConnectionWindow()
        : this(telnet: false)
    {
    }

    /// <summary>
    /// The same window serves "Quick SSH connection" (username@hostname and a password) and
    /// "Quick telnet connection" (hostname or hostname:port, no password).
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
            if (Resources["MachineHint"] is VisualBrush hint && hint.Visual is TextBlock hintText)
            {
                hintText.Text = "hostname[:port] or telnets://hostname";
            }
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
