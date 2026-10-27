using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Picks the top-level folders of a local folder that a synchronization must cover: the tree is built from the
/// folder's own subdirectories, each node carries a checkbox and only the checked ones are returned (as paths
/// relative to the root, which is what <see cref="SmarTTY.Sync.SyncRequest.CleanSelection"/> and the engine expect).
/// Every folder is checked when the window is opened, so the common case (synchronize everything) needs no clicks.
/// </summary>
public partial class SyncFolderSelectionWindow : Window
{
    private const string FolderIcon = "pack://application:,,,/icons/MainWindow/folder_flat.png";

    private readonly string _root;

    private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private SyncFolderSelectionWindow(string root)
    {
        InitializeComponent();
        _root = root;
        Loaded += (object sender, RoutedEventArgs e) => BuildTree();
    }

    /// <summary>
    /// Asks for the folders of <paramref name="localRoot"/> to synchronize. Returns null when the window was
    /// cancelled; returns an empty array when every folder was unchecked (which the caller treats as "nothing to
    /// synchronize"). Every top-level folder is checked on open.
    /// </summary>
    public static string[] Pick(string localRoot)
    {
        if (string.IsNullOrEmpty(localRoot) || !Directory.Exists(localRoot))
        {
            MessageBoxes.Error("Choose the local folder first, so its folders can be listed.");
            return null;
        }
        SyncFolderSelectionWindow window = new SyncFolderSelectionWindow(localRoot);
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return window.SelectedPaths();
    }

    private string[] SelectedPaths()
    {
        List<string> paths = new List<string>();
        foreach (object item in FolderTree.Items)
        {
            if (item is TreeViewItem node && node.Tag is string path && _checked.Contains(path))
            {
                paths.Add(path);
            }
        }
        return paths.ToArray();
    }

    private void BuildTree()
    {
        FolderTree.Items.Clear();
        ImageSource icon = LoadIcon(FolderIcon);
        foreach (string name in LocalFolderTree.GetChildDirectories(_root))
        {
            CheckBox checkBox = new CheckBox { Content = CreateHeader(name, icon), Tag = name };
            checkBox.Checked += Node_Checked;
            checkBox.Unchecked += Node_Unchecked;
            FolderTree.Items.Add(new TreeViewItem { Header = checkBox, Tag = name });
            checkBox.IsChecked = true;
        }
        UpdateSummary();
    }

    private static StackPanel CreateHeader(string caption, ImageSource icon)
    {
        StackPanel header = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            header.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        }
        header.Children.Add(new TextBlock { Text = caption });
        return header;
    }

    private void Node_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is string path)
        {
            _checked.Add(path);
        }
        UpdateSummary();
    }

    private void Node_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is string path)
        {
            _checked.Remove(path);
        }
        UpdateSummary();
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllChecked(true);
    }

    private void CheckNone_Click(object sender, RoutedEventArgs e)
    {
        SetAllChecked(false);
    }

    private void SetAllChecked(bool isChecked)
    {
        foreach (object item in FolderTree.Items)
        {
            if (item is TreeViewItem node && node.Header is CheckBox checkBox)
            {
                checkBox.IsChecked = isChecked;
            }
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int total = FolderTree.Items.Count;
        int selected = SelectedPaths().Length;
        SummaryText.Text = (total == 0) ? "This folder has no subfolders to choose from." : selected + " of " + total + " folders selected";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private static ImageSource LoadIcon(string uri)
    {
        try
        {
            return new BitmapImage(new Uri(uri));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
