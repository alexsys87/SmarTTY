using System.Windows;
using System.Windows.Markup;

namespace SmarTTY.GUI;

public partial class QuickConnectionWindow : Window, IComponentConnector
{
    internal ResourceDictionary ThemeDictionary;

    public QuickConnectionWindow()
    {
        InitializeComponent();
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
