using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Tells the user that minimalistic mode hides most of the controls, and offers not to show it again.
/// </summary>
public partial class MinimalisticModeWindow : Window
{
    /// <summary>Whether the user asked not to see this message again.</summary>
    public bool DontShowAgain
    {
        get
        {
            return DontShowAgainBox.IsChecked == true;
        }
        set
        {
            DontShowAgainBox.IsChecked = value;
        }
    }

    public MinimalisticModeWindow()
    {
        InitializeComponent();
    }
}
