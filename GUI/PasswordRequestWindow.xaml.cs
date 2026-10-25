using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for the SSH password (or the passphrase of a key file) of a connection. It also appears when a password was
/// refused, or over everything else while a sudo prompt waits for it.
/// </summary>
public partial class PasswordRequestWindow : Window
{
    /// <summary>The entered password or passphrase.</summary>
    public string Password => PasswordInput.Password;

    private PasswordRequestWindow(SSHConnectionSettings settings, string previousPassword, bool isPassphrase)
    {
        InitializeComponent();
        if (isPassphrase)
        {
            HeadingText.Text = "Passphrase required for your keyfile";
            PasswordLabel.Text = "Passphrase:";
            Title = "Enter SSH Passphrase";
        }
        else if (previousPassword != null)
        {
            HeadingText.Text = "Password not accepted";
        }
        HostText.Text = settings.HostName;
        UserText.Text = settings.UserName;
    }

    /// <summary>
    /// Asks for the password; returns false if the user cancelled. On success <paramref name="password"/> holds what was
    /// entered.
    /// </summary>
    public static bool ShowPrompt(Window owner, SSHConnectionSettings settings, ref string password, bool isPassphrase, bool isSudo)
    {
        PasswordRequestWindow window = new PasswordRequestWindow(settings, password, isPassphrase)
        {
            Topmost = isSudo
        };
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return false;
        }
        password = window.Password;
        return true;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
