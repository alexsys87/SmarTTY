using SmarTTY.GUI.Backend;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace SmarTTY.GUI;

public partial class ConnectionClosedPopup : UserControl, IComponentConnector
{
    public ConnectionClosedPopup()
    {
        InitializeComponent();
    }

    private void Reconnect(object sender, RoutedEventArgs e)
    {
        (this.DataContext as SessionWrapper.SessionFixer)?.Reconnect();
    }

    private void Ignore(object sender, RoutedEventArgs e)
    {
        (this.DataContext as SessionWrapper.SessionFixer)?.Ignore();
    }

    private void Close(object sender, RoutedEventArgs e)
    {
        (this.DataContext as SessionWrapper.SessionFixer)?.Close();
    }
}
