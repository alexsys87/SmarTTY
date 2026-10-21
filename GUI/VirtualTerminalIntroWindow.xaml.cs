using System.Windows;
using System.Windows.Markup;

namespace SmarTTY.GUI;

public partial class VirtualTerminalIntroWindow : Window, IComponentConnector
{
    public VirtualTerminalIntroWindow()
    {
        InitializeComponent();
    }

    private void Smart_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = true;
    }

    private void Normal_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
    }
}
