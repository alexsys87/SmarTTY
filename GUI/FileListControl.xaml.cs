using SmarTTY.GUI.Backend;
using System;
using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SmarTTY.GUI;

public partial class FileListControl : UserControl, IComponentConnector
{
    public enum FileEvent
    {
        Open,
        Download
    }

    public class FileEventArgs : EventArgs
    {
        public FileEvent Event;

        public string FullPath;
    }

    private int _SelDepth;

    public event EventHandler FocusRequested;

    public event EventHandler<FileEventArgs> FileActionRequested;

    public FileListControl()
    {
        InitializeComponent();
        ResizeFilter();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        ActivateSelection();
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (FileExplorer.ItemWrapper item in ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>())
        {
            if (!item.Entry.IsDirectory)
            {
                FileActionRequested?.Invoke(this, new FileEventArgs
                {
                    FullPath = item.FullPath,
                    Event = FileEvent.Download
                });
            }
        }
    }

    private void TransferList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            _SelDepth++;
            bool flag = false;
            bool flag2 = false;
            foreach (FileExplorer.ItemWrapper item in ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>())
            {
                if (item.Entry.IsDirectory)
                {
                    flag = true;
                }
                else
                {
                    flag2 = true;
                }
            }
            cmdDownload.Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
            cmdEdit.Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
            cmdNavigate.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
            if (((ListBox)TransferList).SelectedItems.Count == 0 && ((CollectionView)TransferList.Items).Count > 0 && _SelDepth < 5)
            {
                ((Selector)TransferList).SelectedItem = TransferList.Items[0];
                FocusFirstItem();
            }
        }
        finally
        {
            _SelDepth--;
        }
    }

    private void FocusFirstItem()
    {
        this.Dispatcher.BeginInvoke((Delegate)(Action)(() =>
        {
            try
            {
                DependencyObject dependencyObject = TransferList.ItemContainerGenerator.ContainerFromIndex(0);
                if (Keyboard.FocusedElement is Window)
                {
                    IInputElement inputElement = dependencyObject as IInputElement;
                    if (inputElement != null)
                    {
                        FocusManager.SetFocusedElement((DependencyObject)this, inputElement);
                    }
                    FocusRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
            }
        }), DispatcherPriority.ContextIdle, new object[0]);
    }

    private void ListView_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key;
        if ((int)key != 2)
        {
            if ((int)key == 6)
            {
                ActivateSelection();
            }
        }
        else
        {
            (this.DataContext as FileExplorer)?.GoToDirectory("..");
        }
    }

    private void ActivateSelection()
    {
        try
        {
            FileExplorer.ItemWrapper[] array = ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>().ToArray();
            foreach (FileExplorer.ItemWrapper itemWrapper in array)
            {
                if (itemWrapper.Entry.IsDirectory)
                {
                    (this.DataContext as FileExplorer)?.GoToDirectory(itemWrapper.Entry.Name);
                }
                else if (itemWrapper.Entry.Size <= 1048576 || MessageBoxes.Ask(itemWrapper.FullPath + " is " + itemWrapper.Size + " in size. Are you sure you want to edit it?"))
                {
                    FileActionRequested?.Invoke(this, new FileEventArgs
                    {
                        FullPath = itemWrapper.FullPath,
                        Event = FileEvent.Open
                    });
                }
            }
        }
        catch (Exception ex)
        {
            MessageBoxes.Exception(ex);
        }
    }

    private void TransferList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelection();
    }

    private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool isActive && this.DataContext is FileExplorer fileExplorer)
        {
            fileExplorer.IsActive = isActive;
        }
        ResizeFilter();
    }

    private void ToolBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeFilter();
    }

    private void ResizeFilter()
    {
        try
        {
            double num = Math.Ceiling((from val in ((IEnumerable)toolBar.Items).OfType<FrameworkElement>()
                                       where (object)val != txtFilter
                                       select val).Sum((FrameworkElement frameworkElement) => frameworkElement.ActualWidth));
            if (num != 0.0)
            {
                txtFilter.Width = toolBar.ActualWidth - num - 32.0;
            }
        }
        catch
        {
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        ActivateSelection();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        (this.DataContext as FileExplorer)?.Refresh();
    }

    private void ContentControl_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        object newValue = e.NewValue;
        bool flag = false;
        int num;
        if (newValue is bool)
        {
            flag = (bool)newValue;
            num = 1;
        }
        else
        {
            num = 0;
        }
        if (((uint)num & (flag ? 1u : 0u)) != 0)
        {
            FocusFirstItem();
        }
    }
}
