using System.Windows;
using System.Windows.Markup;
using VisualTTY;

namespace SmarTTY.GUI;

public partial class VirtualTerminalIntroWindow : Window, IComponentConnector
{
    private ResourceDictionary _ThemeDictionary;

    public VirtualTerminalIntroWindow(GlobalSettings settings)
    {
        InitializeComponent();
        MainWindow.ApplyTheme((Window)this, settings.Theme, ref _ThemeDictionary);
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
