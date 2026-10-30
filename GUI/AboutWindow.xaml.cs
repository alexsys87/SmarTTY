using System.Diagnostics;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// The "About SmarTTY" dialog, with the version, the copyright, the credits (the SSH.NET library and the Oxygen
/// icon set) and the links to the home page and the SSH.NET project.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>Replaces the version line; null keeps the version built into the markup.</summary>
    public static string VersionTextOverride;

    public AboutWindow()
    {
        InitializeComponent();
        if (VersionTextOverride != null)
        {
            VersionText.Text = VersionTextOverride;
        }
    }

    private void Homepage_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("http://sysprogs.com/");
    }

    private void SshNet_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/sshnet/SSH.NET");
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }
}
