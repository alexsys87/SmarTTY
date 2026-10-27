using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmarTTY.GUI;

/// <summary>
/// Browses the folder tree of the local machine (the WPF counterpart of <see cref="RemoteFolderBrowserWindow"/>):
/// "This PC" holds the drives and the user's shell folders, the subfolders of a node are listed when it is first
/// expanded, and a path can be typed, refreshed or extended with a new folder. The rules of the tree (roots, children,
/// ordering, path normalization) are in <see cref="LocalFolderTree"/>, which has no UI dependency.
/// </summary>
public partial class LocalFolderBrowserWindow : Window
{
    private const string FolderIcon = "pack://application:,,,/icons/MainWindow/folder_flat.png";

    private const string ComputerIcon = "pack://application:,,,/icons/computer.png";

    // A node carries this empty placeholder child until its real children are listed, so it shows an expander.
    private const string PlaceholderHeader = "";

    private readonly ImageSource _folderImage = LoadIcon(FolderIcon);

    private readonly ImageSource _computerImage = LoadIcon(ComputerIcon);

    private string _initialPath;

    /// <summary>The path shown in the folder box: the typed one, or the selected node.</summary>
    public string SelectedPath => FolderBox.Text;

    private LocalFolderBrowserWindow()
    {
        InitializeComponent();
        Loaded += (object sender, RoutedEventArgs e) => BuildRoots();
    }

    /// <summary>Asks for a folder on the local machine; returns null when the window was cancelled.</summary>
    public static string Browse(string initialPath = null, string title = null, string description = null)
    {
        LocalFolderBrowserWindow window = new LocalFolderBrowserWindow();
        if (!string.IsNullOrEmpty(title))
        {
            window.Title = title;
        }
        if (!string.IsNullOrEmpty(description))
        {
            window.DescriptionText.Text = description;
            window.DescriptionText.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(initialPath))
        {
            window._initialPath = LocalFolderTree.NormalizePath(initialPath, null) ?? initialPath;
        }
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return window.SelectedPath;
    }

    /// <summary>Builds the tree: a "This PC" node with the drives and shell folders under it.</summary>
    private void BuildRoots()
    {
        FolderTree.Items.Clear();
        TreeViewItem thisPc = CreateNode("This PC", null, _computerImage);
        foreach (LocalFolderEntry root in LocalFolderTree.GetRoots())
        {
            if (root.Path != null)
            {
                thisPc.Items.Add(CreateNode(root.Name, root.Path, _folderImage));
            }
        }
        FolderTree.Items.Add(thisPc);
        thisPc.IsExpanded = true;
        thisPc.IsSelected = true;
        FolderBox.Text = "";
        if (!string.IsNullOrEmpty(_initialPath))
        {
            SelectPath(_initialPath);
        }
        else
        {
            FolderTree.Focus();
        }
    }

    private TreeViewItem CreateNode(string caption, string path, ImageSource icon)
    {
        StackPanel header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(new TextBlock { Text = caption });
        TreeViewItem node = new TreeViewItem { Header = header, Tag = path };
        if (path != null)
        {
            // A folder shows an expander even before its children are listed; "This PC" gets its real children at once.
            node.Items.Add(new TreeViewItem { Header = PlaceholderHeader });
            node.Expanded += (object sender, RoutedEventArgs e) => LoadChildren((TreeViewItem)sender);
        }
        return node;
    }

    private void LoadChildren(TreeViewItem treeNode, bool refresh = false)
    {
        string path = treeNode == null ? null : (string)treeNode.Tag;
        if (path == null)
        {
            return;
        }
        if (!refresh && !HasOnlyPlaceholder(treeNode))
        {
            return;
        }
        treeNode.Items.Clear();
        foreach (string name in LocalFolderTree.GetChildDirectories(path))
        {
            string childPath = LocalFolderTree.GetChildPath(path, name);
            if (childPath != null)
            {
                treeNode.Items.Add(CreateNode(name, childPath, _folderImage));
            }
        }
    }

    /// <summary>Expands the tree down to <paramref name="path"/> and selects the folder.</summary>
    private void SelectPath(string path)
    {
        List<string> segments = LocalFolderTree.GetPathSegments(path);
        TreeViewItem current = (segments.Count > 0) ? FindTopLevel(segments[0]) : null;
        if (current == null)
        {
            FolderBox.Text = path;
            return;
        }
        current.IsExpanded = true;
        current.IsSelected = true;
        for (int index = 1; index < segments.Count; index++)
        {
            LoadChildren(current);
            TreeViewItem child = FindChild(current, segments[index]);
            if (child == null)
            {
                break;
            }
            child.IsExpanded = true;
            child.IsSelected = true;
            current = child;
        }
        FolderBox.Text = path;
    }

    /// <summary>Finds the node that stands for the first path segment: a drive or shell folder under "This PC".</summary>
    private TreeViewItem FindTopLevel(string segment)
    {
        foreach (object item in FolderTree.Items)
        {
            if (!(item is TreeViewItem node))
            {
                continue;
            }
            if (MatchesPath(node, segment))
            {
                return node;
            }
            foreach (object child in node.Items)
            {
                if (child is TreeViewItem descendant && MatchesPath(descendant, segment))
                {
                    node.IsExpanded = true;
                    return descendant;
                }
            }
        }
        return null;
    }

    private static bool MatchesPath(TreeViewItem node, string path)
    {
        return node.Tag is string nodePath && string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase);
    }

    private static TreeViewItem FindChild(TreeViewItem parent, string path)
    {
        foreach (object item in parent.Items)
        {
            if (item is TreeViewItem child && MatchesPath(child, path))
            {
                return child;
            }
        }
        return null;
    }

    private static bool HasOnlyPlaceholder(TreeViewItem node)
    {
        return node.Items.Count == 1 && node.Items[0] is TreeViewItem placeholder && placeholder.Tag == null &&
               placeholder.Header is string header && header == PlaceholderHeader;
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is TreeViewItem selected && selected.Tag is string path)
        {
            FolderBox.Text = path;
        }
    }

    private void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            NavigateTo(FolderBox.Text);
            e.Handled = true;
        }
    }

    private void NavigateTo(string typedPath)
    {
        string path = LocalFolderTree.NormalizePath(typedPath, FolderBox.Text);
        if (path == null || !Directory.Exists(path))
        {
            MessageBoxes.Error("The folder does not exist:\n" + (typedPath ?? ""));
            return;
        }
        SelectPath(path);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is TreeViewItem selected && selected.Tag is string)
        {
            LoadChildren(selected, refresh: true);
        }
        else
        {
            BuildRoots();
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        string parent = FolderBox.Text;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            MessageBoxes.Error("Select the folder to create the new one in.");
            return;
        }
        string name = RemoteNameWindow.Ask(this, "New folder", "Name of the new folder in " + parent + ":", "New folder");
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        string newPath = LocalFolderTree.GetChildPath(parent, name);
        if (newPath == null || Directory.Exists(newPath))
        {
            MessageBoxes.Error("A folder named '" + name + "' already exists, or the name is not valid.");
            return;
        }
        try
        {
            Directory.CreateDirectory(newPath);
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
            return;
        }
        if (FolderTree.SelectedItem is TreeViewItem selected && selected.Tag is string selectedPath &&
            string.Equals(selectedPath, parent, StringComparison.OrdinalIgnoreCase))
        {
            LoadChildren(selected, refresh: true);
        }
        FolderBox.Text = newPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string path = FolderBox.Text;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            MessageBoxes.Error("The folder does not exist:\n" + (path ?? ""));
            return;
        }
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
