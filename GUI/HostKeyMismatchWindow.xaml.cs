using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Warns that the host key of a server has changed since the last connection. A changed key can mean a man-in-the-middle
/// attack, so continuing needs a second confirmation and the default answer is to abort.
/// </summary>
public partial class HostKeyMismatchWindow : Window
{
    private HostKeyMismatchWindow(SSHConnectionSettings settings, string typeChangeText)
    {
        InitializeComponent();
        HostText.Text = settings.HostName;
        if (typeChangeText != null)
        {
            TypeChangeText.Text = typeChangeText;
            HeadingText.Text = "Host key type changed";
            DetailedText.Text = "SmarTTY has detected that the type of the public key of the server has changed. This could be caused by upgrading the server, or the SSH client library, but might also indicate that someone is eavesdropping on you.";
        }
    }

    /// <summary>Returns true when the user chose to remember the new key and continue.</summary>
    public static bool Confirm(SSHConnectionSettings settings, string typeChangeText)
    {
        HostKeyMismatchWindow window = new HostKeyMismatchWindow(settings, typeChangeText);
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // The same second confirmation the WinForms dialog asked for. Abort is the default button, so Enter never trusts the new key.
        if (!MessageBoxes.AskWarn("Are you sure? If the remote computer has been compromised, the attacker might be able to capture your password!"))
        {
            return;
        }
        DialogResult = true;
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
