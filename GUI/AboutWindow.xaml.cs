using System.Diagnostics;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// The "About SmarTTY" dialog, with the version, the copyright and the link to the home page.
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
        Process.Start(new ProcessStartInfo("http://sysprogs.com/")
        {
            UseShellExecute = true
        });
    }
}
