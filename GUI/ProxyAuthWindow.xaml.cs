using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Asks for the credentials of the proxy that refused the connection without them: an HTTP proxy that answered with
/// "407 Proxy Authentication Required", or a SOCKS5 proxy that rejected the user name and password.
/// </summary>
public partial class ProxyAuthWindow : Window
{
    private ProxyAuthWindow(string realm, string credentials)
    {
        InitializeComponent();
        RealmText.Text = realm;
        if (credentials != null)
        {
            int colon = credentials.IndexOf(':');
            if (colon == -1)
            {
                UserNameInput.Text = credentials;
            }
            else
            {
                UserNameInput.Text = credentials.Substring(0, colon);
                PasswordInput.Password = credentials.Substring(colon + 1);
            }
        }
        Loaded += (object sender, RoutedEventArgs e) => UserNameInput.Focus();
    }

    /// <summary>
    /// Asks for the credentials; returns false if the user cancelled. On success <paramref name="credentials"/> holds
    /// "user:password".
    /// </summary>
    public static bool ShowPrompt(Window owner, string realm, ref string credentials)
    {
        ProxyAuthWindow window = new ProxyAuthWindow(realm, credentials);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return false;
        }
        credentials = window.UserNameInput.Text + ":" + window.PasswordInput.Password;
        return true;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
