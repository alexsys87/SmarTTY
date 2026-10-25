using System.Windows;
using VisualTTY;

namespace SmarTTY.GUI;

/// <summary>
/// Asks whether to save the host key of a server that is connected to for the first time. The key is saved only when
/// the user agrees, so that a later change of the key can be detected.
/// </summary>
public partial class NewHostKeyWindow : Window
{
    private NewHostKeyWindow(SSHConnectionSettings settings, string fingerprint)
    {
        InitializeComponent();
        HostText.Text = settings.HostName;
        KeyText.Text = fingerprint;
    }

    /// <summary>Returns true when the user chose to save the key.</summary>
    public static bool Confirm(SSHConnectionSettings settings, string fingerprint)
    {
        NewHostKeyWindow window = new NewHostKeyWindow(settings, fingerprint);
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
