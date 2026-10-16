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
        object obj = this.Resources[(object)"hint"];
        _ToolTop = obj as ToolTip;
    }

    private void ListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SuggestionPopupController obj = this.DataContext as SuggestionPopupController;
        if (obj != null)
        {
            object originalSource = ((RoutedEventArgs)e).OriginalSource;
            object obj2 = ((originalSource is FrameworkElement) ? originalSource : null);
            obj.OnItemMouseDown(((obj2 != null) ? ((FrameworkElement)obj2).DataContext : null) as SuggestionPopupController.Item);
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
        object obj = e.AddedItems[0];
        listBox.ScrollIntoView(obj);
        DependencyObject dependencyObject = ((ItemsControl)listBox).ItemContainerGenerator.ContainerFromItem(obj);
        ListBoxItem lbi = dependencyObject as ListBoxItem;
        if (lbi == null)
        {
            return;
        }
        SuggestionPopupController.Item castItem = obj as SuggestionPopupController.Item;
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
        object obj = ((sender is FrameworkElement) ? sender : null);
        if ((((obj != null) ? ((FrameworkElement)obj).DataContext : null) as SuggestionPopupController.Item)?.Entry.Summary is VirtualTerminalPopupSummary.FileOrDirectoryInfo fdi)
        {
            _Controller.HandleShortcut(fdi);
        }
    }
}
