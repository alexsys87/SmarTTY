using SmarTTY.GUI.Backend;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using VisualTTY;

namespace SmarTTY.GUI;

public partial class VirtualTerminalPopup : UserControl, IComponentConnector, IStyleConnector
{
    private MainWindowController _Controller;

    private ToolTip _ToolTop;

    public VirtualTerminalPopup(MainWindowController controller)
    {
        InitializeComponent();
        _Controller = controller;
        object sender = this.Resources[(object)"hint"];
        _ToolTop = sender as ToolTip;
    }

    private void ListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SuggestionPopupController controller = this.DataContext as SuggestionPopupController;
        if (controller != null)
        {
            object originalSource = ((RoutedEventArgs)e).OriginalSource;
            object sourceElement = ((originalSource is FrameworkElement) ? originalSource : null);
            controller.OnItemMouseDown(((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as SuggestionPopupController.Item);
        }
    }

    private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ToolTop.IsOpen = false;
        if ((e.AddedItems?.Count ?? 0) <= 0)
        {
            return;
        }
        ListBox listBox = sender as ListBox;
        if (listBox == null)
        {
            return;
        }
        object selectedItem = e.AddedItems[0];
        listBox.ScrollIntoView(selectedItem);
        DependencyObject dependencyObject = ((ItemsControl)listBox).ItemContainerGenerator.ContainerFromItem(selectedItem);
        ListBoxItem lbi = dependencyObject as ListBoxItem;
        if (lbi == null)
        {
            return;
        }
        SuggestionPopupController.Item castItem = selectedItem as SuggestionPopupController.Item;
        if (castItem != null && castItem.Entry.Summary != null)
        {
            this.Dispatcher.BeginInvoke((Delegate)(Action)(() =>
            {
                _ToolTop.PlacementTarget = (UIElement)lbi;
                ((FrameworkElement)_ToolTop).DataContext = castItem.Entry.Summary;
                _ToolTop.IsOpen = true;
            }), DispatcherPriority.ContextIdle, new object[0]);
        }
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        object sourceElement = ((sender is FrameworkElement) ? sender : null);
        if ((((sourceElement != null) ? ((FrameworkElement)sourceElement).DataContext : null) as SuggestionPopupController.Item)?.Entry.Summary is VirtualTerminalPopupSummary.FileOrDirectoryInfo fdi)
        {
            _Controller.HandleShortcut(fdi);
        }
    }
}
