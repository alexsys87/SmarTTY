using SmarTTY.Forms;
using SmarTTY.GUI.Backend;
using SmarTTY.IO;
using SmarTTY.LocalRepo;
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
        Download,
        DownloadArchive,
        Run,
        Follow,
        Upload,
        InstallPackage
    }

    public class FileEventArgs : EventArgs
    {
        public FileEvent Event;

        /// <summary>
        /// The remote file; for <see cref="FileEvent.Upload"/> the remote directory to upload into.
        /// </summary>
        public string FullPath;

        /// <summary>
        /// The local files and folders to upload (<see cref="FileEvent.Upload"/> only).
        /// </summary>
        public string[] LocalFiles;

        /// <summary>
        /// The remote files to install (<see cref="FileEvent.InstallPackage"/>), download (<see cref="FileEvent.Download"/>) or the remote folders to download as archives (<see cref="FileEvent.DownloadArchive"/>).
        /// </summary>
        public string[] FullPaths;
    }

    private int _SelDepth;

    private Point? _DragStart;

    /// <summary>
    /// True while files of the panel are being dragged out. The drag carries local copies of the files, so every drop target
    /// of this program has to refuse it; otherwise dropping on the terminal or the panel would upload the files again.
    /// </summary>
    internal static bool IsDraggingOut { get; private set; }

    public event EventHandler FocusRequested;

    public event EventHandler<FileEventArgs> FileActionRequested;

    public FileListControl()
    {
        InitializeComponent();
        ResizeFilter();
    }

    /// <summary>
    /// The remote directory a drop at the current mouse position goes to: the folder under the mouse, or the
    /// directory the panel shows.
    /// </summary>
    private string GetDropDirectory(DragEventArgs e)
    {
        DependencyObject element = e.OriginalSource as DependencyObject;
        while (element != null && !(element is ListViewItem))
        {
            element = (element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (element is ListViewItem listViewItem && listViewItem.DataContext is FileExplorer.ItemWrapper item && item.Entry.IsDirectory && item.Name != "..")
        {
            return item.FullPath;
        }
        return (this.DataContext as FileExplorer)?.WorkingDirectory;
    }

    private void TransferList_DragOver(object sender, DragEventArgs e)
    {
        bool canDrop = !IsDraggingOut && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) && GetDropDirectory(e) != null;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TransferList_Drop(object sender, DragEventArgs e)
    {
        // Handled here, so that the main window does not upload the same files to the terminal's directory as well.
        e.Handled = true;
        if (IsDraggingOut)
        {
            return;
        }
        string directory = GetDropDirectory(e);
        if (directory == null || !(e.Data?.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0)
        {
            return;
        }
        FileActionRequested?.Invoke(this, new FileEventArgs
        {
            Event = FileEvent.Upload,
            FullPath = directory,
            LocalFiles = files
        });
    }

    private void TransferList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A drag starts only on a selected item, so that a click-and-drag on empty space or an unselected item still selects.
        DependencyObject element = e.OriginalSource as DependencyObject;
        while (element != null && !(element is ListViewItem) && !(element is System.Windows.Controls.Primitives.ScrollBar))
        {
            element = (element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        bool onItem = element is ListViewItem;
        _DragStart = onItem ? e.GetPosition(null) : (Point?)null;
    }

    private void TransferList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _DragStart = null;
    }

    private void TransferList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _DragStart == null || IsDraggingOut)
        {
            return;
        }
        Point position = e.GetPosition(null);
        if (Math.Abs(position.X - _DragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _DragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _DragStart = null;
        FileExplorer explorer = Explorer;
        FileExplorer.ItemWrapper[] selected = SelectedEntries();
        if (explorer == null || selected.Length == 0)
        {
            return;
        }
        try
        {
            IsDraggingOut = true;
            string[] paths = explorer.StageForDrag(selected);
            if (paths == null || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(TransferList, data, DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
        finally
        {
            IsDraggingOut = false;
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        ActivateSelection();
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        string[] files = SelectedEntries().Where((FileExplorer.ItemWrapper item) => !item.Entry.IsDirectory).Select((FileExplorer.ItemWrapper item) => item.FullPath).ToArray();
        if (files.Length != 0)
        {
            // One event for all files, so that the download directory is asked for once and not once per file.
            FileActionRequested?.Invoke(this, new FileEventArgs
            {
                FullPaths = files,
                Event = FileEvent.Download
            });
        }
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        FileExplorer.ItemWrapper script = SelectedScript();
        if (script != null)
        {
            FileActionRequested?.Invoke(this, new FileEventArgs
            {
                FullPath = script.FullPath,
                Event = FileEvent.Run
            });
        }
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        FileExplorer.ItemWrapper file = SelectedFile();
        if (file != null)
        {
            FileActionRequested?.Invoke(this, new FileEventArgs
            {
                FullPath = file.FullPath,
                Event = FileEvent.Follow
            });
        }
    }

    /// <summary>The selected item if it is the only one and a file (not a folder), otherwise null.</summary>
    private FileExplorer.ItemWrapper SelectedFile()
    {
        FileExplorer.ItemWrapper[] selected = ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>().ToArray();
        return selected.Length == 1 && !selected[0].Entry.IsDirectory ? selected[0] : null;
    }

    /// <summary>The selected file if it is the only selected item and a shell script, otherwise null.</summary>
    private FileExplorer.ItemWrapper SelectedScript()
    {
        FileExplorer.ItemWrapper[] selected = ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>().ToArray();
        return selected.Length == 1 && !selected[0].Entry.IsDirectory && RemoteScript.IsScriptFileName(selected[0].Name) ? selected[0] : null;
    }

    /// <summary>
    /// The selected files that the package manager of the OpenWrt host installs (.ipk for opkg, .apk for apk); none when the
    /// host is not OpenWrt.
    /// </summary>
    private FileExplorer.ItemWrapper[] InstallablePackages(LocalRepoPackageManager? manager)
    {
        if (manager == null)
        {
            return new FileExplorer.ItemWrapper[0];
        }
        return SelectedEntries().Where((FileExplorer.ItemWrapper item) => !item.Entry.IsDirectory && RemotePackageInstall.CanInstall(item.Name, manager)).ToArray();
    }

    /// <summary>
    /// Shows "Install package (opkg)" or "Install package (apk)" when the selection holds packages that the OpenWrt host can
    /// install. The package manager is known only after the probe of the session has answered, so this runs again when the
    /// menu opens.
    /// </summary>
    private void UpdatePackageMenu()
    {
        LocalRepoPackageManager? manager = Explorer?.PackageManager;
        bool any = InstallablePackages(manager).Length > 0;
        cmdInstallIpk.Visibility = any && manager == LocalRepoPackageManager.Opkg ? Visibility.Visible : Visibility.Collapsed;
        cmdInstallApk.Visibility = any && manager == LocalRepoPackageManager.Apk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallPackage_Click(object sender, RoutedEventArgs e)
    {
        string[] packages = InstallablePackages(Explorer?.PackageManager).Select((FileExplorer.ItemWrapper item) => item.FullPath).ToArray();
        if (packages.Length == 0)
        {
            return;
        }
        FileActionRequested?.Invoke(this, new FileEventArgs
        {
            Event = FileEvent.InstallPackage,
            FullPaths = packages
        });
    }

    private void TransferList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdatePackageMenu();
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        Explorer?.Extract(SelectedEntries());
    }

    private void DownloadArchive_Click(object sender, RoutedEventArgs e)
    {
        string[] folders = SelectedEntries().Where((FileExplorer.ItemWrapper item) => item.Entry.IsDirectory).Select((FileExplorer.ItemWrapper item) => item.FullPath).ToArray();
        if (folders.Length != 0)
        {
            FileActionRequested?.Invoke(this, new FileEventArgs
            {
                FullPaths = folders,
                Event = FileEvent.DownloadArchive
            });
        }
    }

    private void TransferList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            _SelDepth++;
            bool directorySelectionChanged = false;
            bool archivableSelected = false;
            bool extractableSelected = false;
            bool fileSelectionChanged = false;
            foreach (FileExplorer.ItemWrapper item in ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>())
            {
                if (item.Entry.IsDirectory)
                {
                    directorySelectionChanged = true;
                    // ".." is the parent directory, not a folder to download.
                    archivableSelected |= item.Name != "..";
                }
                else
                {
                    fileSelectionChanged = true;
                    extractableSelected |= RemoteFileOperations.IsExtractableArchive(item.Name);
                }
            }
            cmdDownload.Visibility = ((!fileSelectionChanged) ? Visibility.Collapsed : Visibility.Visible);
            cmdEdit.Visibility = ((!fileSelectionChanged) ? Visibility.Collapsed : Visibility.Visible);
            // Scripts, tar and chmod are the tools of a Unix host.
            bool windowsHost = Explorer?.IsWindowsHost == true;
            cmdRun.Visibility = !windowsHost && SelectedScript() != null ? Visibility.Visible : Visibility.Collapsed;
            cmdFollow.Visibility = !windowsHost && SelectedFile() != null ? Visibility.Visible : Visibility.Collapsed;
            cmdExtract.Visibility = !windowsHost && extractableSelected ? Visibility.Visible : Visibility.Collapsed;
            UpdatePackageMenu();
            // A Windows host has no tar: its folder is downloaded as a folder, file by file.
            cmdDownloadArchive.Header = windowsHost ? "Download folder..." : "Download folder as .tar.gz...";
            cmdDownloadArchive.Visibility = archivableSelected ? Visibility.Visible : Visibility.Collapsed;
            cmdNavigate.Visibility = ((!directorySelectionChanged) ? Visibility.Collapsed : Visibility.Visible);
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
        if (HandleFileOperationKey(e))
        {
            e.Handled = true;
            return;
        }
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
            FileExplorer.ItemWrapper[] items = ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>().ToArray();
            // Names of folders are relative to the directory that is shown, so only one folder can be entered; with a folder
            // in the selection nothing else is opened.
            FileExplorer.ItemWrapper folder = items.FirstOrDefault((FileExplorer.ItemWrapper item) => item.Entry.IsDirectory);
            if (folder != null)
            {
                (this.DataContext as FileExplorer)?.GoToDirectory(folder.Entry.Name);
                return;
            }
            foreach (FileExplorer.ItemWrapper itemWrapper in items)
            {
                if (itemWrapper.Entry.Size <= 1048576 || MessageBoxes.Ask(itemWrapper.FullPath + " is " + itemWrapper.Size + " in size. Are you sure you want to edit it?"))
                {
                    FileActionRequested?.Invoke(this, new FileEventArgs
                    {
                        FullPath = itemWrapper.FullPath,
                        Event = FileEvent.Open
                    });
                }
            }
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void TransferList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on the header, a scroll bar or the empty space below the items must not open the selected item.
        DependencyObject element = e.OriginalSource as DependencyObject;
        while (element != null && !(element is ListViewItem))
        {
            element = (element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (element == null)
        {
            return;
        }
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
            double value = Math.Ceiling((from val in ((IEnumerable)toolBar.Items).OfType<FrameworkElement>()
                                       where (object)val != txtFilter
                                       select val).Sum((FrameworkElement frameworkElement) => frameworkElement.ActualWidth));
            if (value != 0.0)
            {
                txtFilter.Width = toolBar.ActualWidth - value - 32.0;
            }
        }
        catch
        {
        }
    }

    private FileExplorer Explorer => this.DataContext as FileExplorer;

    // The selected files and folders, without "..".
    private FileExplorer.ItemWrapper[] SelectedEntries()
    {
        return ((ListBox)TransferList).SelectedItems.OfType<FileExplorer.ItemWrapper>().Where((FileExplorer.ItemWrapper item) => item.Name != "..").ToArray();
    }

    // Delete, F2, Ctrl+C/X/V and Ctrl+Shift+N in the list, as in Explorer.
    private bool HandleFileOperationKey(KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && e.Key == Key.Delete)
        {
            DeleteSelection();
            return true;
        }
        if (modifiers == ModifierKeys.None && e.Key == Key.F2)
        {
            RenameSelection();
            return true;
        }
        if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C:
                    Explorer?.CopyToClipboard(SelectedEntries(), cut: false);
                    return true;
                case Key.X:
                    Explorer?.CopyToClipboard(SelectedEntries(), cut: true);
                    return true;
                case Key.V:
                    Explorer?.Paste();
                    return true;
            }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            CreateEntry(folder: true);
            return true;
        }
        return false;
    }

    private void XferMenu_Opened(object sender, RoutedEventArgs e)
    {
        FileExplorer.ItemWrapper[] selected = SelectedEntries();
        FileExplorer explorer = Explorer;
        cmdCut.IsEnabled = selected.Length > 0;
        cmdCopy.IsEnabled = selected.Length > 0;
        cmdDelete.IsEnabled = selected.Length > 0;
        cmdRename.IsEnabled = selected.Length == 1;
        cmdPaste.IsEnabled = explorer != null && explorer.CanPaste;
        cmdNewFolder.IsEnabled = explorer != null;
        cmdNewFile.IsEnabled = explorer != null;
        // chmod means nothing on a Windows host.
        cmdMakeExecutable.IsEnabled = explorer != null && !explorer.IsWindowsHost && selected.Length > 0;
        cmdPermissions.IsEnabled = explorer != null && !explorer.IsWindowsHost && selected.Length > 0;
        UpdatePackageMenu();
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        Explorer?.CopyToClipboard(SelectedEntries(), cut: true);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Explorer?.CopyToClipboard(SelectedEntries(), cut: false);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        Explorer?.Paste();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        RenameSelection();
    }

    private void MakeExecutable_Click(object sender, RoutedEventArgs e)
    {
        Explorer?.MakeExecutable(SelectedEntries());
    }

    private void Permissions_Click(object sender, RoutedEventArgs e)
    {
        FileExplorer explorer = Explorer;
        FileExplorer.ItemWrapper[] selected = SelectedEntries();
        if (explorer == null || selected.Length == 0)
        {
            return;
        }
        string prompt = selected.Length == 1 ? "Permissions of '" + selected[0].Name + "':" : "Permissions of " + selected.Length + " selected items:";
        RemotePermissionsWindow.Request request = RemotePermissionsWindow.Ask(Window.GetWindow(this), prompt, selected.Select((FileExplorer.ItemWrapper item) => RemotePermissions.ParseMode(item.Entry.Mode)).ToArray(), selected.Any((FileExplorer.ItemWrapper item) => item.Entry.IsDirectory));
        if (request != null)
        {
            explorer.ChangePermissions(selected, request.Change, request.Scope);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelection();
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        CreateEntry(folder: true);
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        CreateEntry(folder: false);
    }

    private void DeleteSelection()
    {
        try
        {
            Explorer?.Delete(SelectedEntries());
        }
        catch (Exception exception)
        {
            MessageBoxes.Exception(exception);
        }
    }

    private void RenameSelection()
    {
        FileExplorer explorer = Explorer;
        FileExplorer.ItemWrapper[] selected = SelectedEntries();
        if (explorer == null || selected.Length != 1)
        {
            return;
        }
        string newName = RemoteNameWindow.Ask(Window.GetWindow(this), "Rename", "New name for '" + selected[0].Name + "':", selected[0].Name, selectStemOnly: !selected[0].Entry.IsDirectory);
        if (newName != null)
        {
            explorer.Rename(selected[0], newName);
        }
    }

    private void CreateEntry(bool folder)
    {
        FileExplorer explorer = Explorer;
        if (explorer == null)
        {
            return;
        }
        string name = RemoteNameWindow.Ask(Window.GetWindow(this), folder ? "New folder" : "New empty file", (folder ? "Name of the new folder in " : "Name of the new file in ") + explorer.WorkingDirectory + ":", explorer.SuggestNewName(folder ? "New folder" : "new file.txt", folder), selectStemOnly: !folder);
        if (name != null)
        {
            explorer.CreateEntry(name, folder);
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
        bool isEnabled = false;
        int value;
        if (newValue is bool)
        {
            isEnabled = (bool)newValue;
            value = 1;
        }
        else
        {
            value = 0;
        }
        if (((uint)value & (isEnabled ? 1u : 0u)) != 0)
        {
            FocusFirstItem();
        }
    }
}
