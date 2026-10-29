using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SmarTTY.GUI;

/// <summary>
/// Browses the directory tree of a remote host. Children are listed with <c>ls</c> when a node is first expanded; a
/// listing that does not finish right away keeps the window busy until the polling timer collects it.
/// </summary>
public partial class RemoteFolderBrowserWindow : Window
{
    // A node carries this empty placeholder child until its real children are listed, so it shows an expander.
    private const string PlaceholderHeader = "";

    private readonly bool _usePrefixedListing;

    private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

    private RemoteCommandStarter _runCommand;

    private IRemoteCommand _pendingJob;

    private string _initialPath;

    private TreeViewItem _loadingNode;

    /// <summary>The path shown in the folder box: the typed one, or the selected node.</summary>
    public string SelectedPath => FolderBox.Text;

    private bool IsPolling
    {
        set
        {
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    private RemoteFolderBrowserWindow(bool usePrefixedListing)
    {
        InitializeComponent();
        _usePrefixedListing = usePrefixedListing;
        _timer.Tick += OnPollTimer;
        Loaded += (object sender, RoutedEventArgs e) => OnLoad();
    }

    /// <summary>Asks for a directory on the remote host; returns null when the window was cancelled.</summary>
    public static string Browse(RemoteCommandStarter callback, bool usePrefixedListing, string initialPath = null, string hostDisplayName = null)
    {
        if (string.IsNullOrEmpty(initialPath))
        {
            initialPath = RemoteListing.GetWorkingDirectory(callback);
        }
        RemoteFolderBrowserWindow window = new RemoteFolderBrowserWindow(usePrefixedListing)
        {
            _runCommand = callback,
            _initialPath = initialPath,
            Title = "Select a Folder on " + (hostDisplayName ?? "remote host")
        };
        WindowOwnership.Attach(window, WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        return window.SelectedPath;
    }

    private void OnLoad()
    {
        InitializeTree();
        if (!string.IsNullOrEmpty(_initialPath))
        {
            SelectPath(_initialPath);
        }
    }

    private void ShowBusy()
    {
        if (BusyPanel.Visibility != Visibility.Visible)
        {
            BusyPanel.Visibility = Visibility.Visible;
            DirectoryTree.IsEnabled = false;
            OkButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
        }
    }

    private void HideBusy()
    {
        if (BusyPanel.Visibility == Visibility.Visible)
        {
            BusyPanel.Visibility = Visibility.Collapsed;
            DirectoryTree.IsEnabled = true;
            OkButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void InitializeTree()
    {
        TreeViewItem root = CreateNode("/", "/");
        DirectoryTree.Items.Add(root);
        root.IsExpanded = true;
        LoadChildren(root);
    }

    private void LoadChildren(TreeViewItem treeNode, bool clearNodes = false)
    {
        if (treeNode == null || _runCommand == null || treeNode.Items.Count != 1 || !IsPlaceholder(treeNode.Items[0]))
        {
            return;
        }
        if (clearNodes)
        {
            treeNode.Items.Clear();
        }
        string fullPath = "/" + ((string)treeNode.Tag).TrimStart('/');
        _loadingNode = treeNode;
        try
        {
            IRemoteCommand remoteCommand = null;
            _runCommand("ls", (_usePrefixedListing ? "-F" : "-1pLA") + " \"" + fullPath + "\"", ref remoteCommand);
            if (remoteCommand == null)
            {
                return;
            }
            if (remoteCommand.WaitForExit(300))
            {
                using (remoteCommand)
                {
                    if (remoteCommand.ExitCode == 0 || remoteCommand.OutputLines.Count != 0)
                    {
                        if (_usePrefixedListing && remoteCommand.OutputLines[0].StartsWith("opendir failed"))
                        {
                            PopulateChildren(remoteCommand.OutputLines, remoteCommand.ErrorText);
                            throw new Exception(remoteCommand.OutputLines[0]);
                        }
                        IsPolling = false;
                        PopulateChildren(remoteCommand.OutputLines, remoteCommand.ErrorText);
                        _loadingNode = null;
                        _pendingJob = null;
                    }
                    else
                    {
                        IsPolling = false;
                        MessageBoxes.Error(RemoteListing.DescribeFailure(remoteCommand.ExitCode, remoteCommand.ErrorText));
                        _pendingJob = null;
                        _loadingNode = null;
                    }
                    return;
                }
            }
            ShowBusy();
            _pendingJob = remoteCommand;
            IsPolling = true;
        }
        catch (Exception exception)
        {
            MessageBoxes.Error(exception.Message);
            HideBusy();
            _pendingJob = null;
            IsPolling = false;
            _loadingNode = null;
        }
    }

    private void OnPollTimer(object sender, EventArgs e)
    {
        if (_pendingJob == null)
        {
            IsPolling = false;
            _loadingNode = null;
            HideBusy();
        }
        else if (_pendingJob.WaitForExit(0))
        {
            HideBusy();
            if (_pendingJob.ExitCode == 0 || _pendingJob.OutputLines.Count != 0)
            {
                IsPolling = false;
                PopulateChildren(_pendingJob.OutputLines, _pendingJob.ErrorText);
                _loadingNode = null;
            }
            else
            {
                IsPolling = false;
                MessageBoxes.Error(RemoteListing.DescribeFailure(_pendingJob.ExitCode, _pendingJob.ErrorText));
                _loadingNode = null;
            }
            _pendingJob.Dispose();
            _pendingJob = null;
        }
    }

    private void PopulateChildren(List<string> lines, string errorText)
    {
        if (_loadingNode == null)
        {
            return;
        }
        if (_loadingNode.Items.Count == 1 && IsPlaceholder(_loadingNode.Items[0]))
        {
            _loadingNode.Items.Clear();
        }
        if (string.IsNullOrEmpty(errorText))
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = errorText;
        }
        if (lines == null)
        {
            return;
        }
        if (_usePrefixedListing)
        {
            lines = RemoteListing.SortEntries(lines, isPrefixed: true);
        }
        foreach (string item in lines)
        {
            if (_usePrefixedListing)
            {
                if (item.StartsWith("d "))
                {
                    string name = item.Substring(2);
                    _loadingNode.Items.Add(CreateNode(name, ChildPath(_loadingNode, name)));
                }
            }
            else if (item.EndsWith("/"))
            {
                string name = item.Substring(0, item.Length - 1);
                _loadingNode.Items.Add(CreateNode(name, ChildPath(_loadingNode, name)));
            }
        }
    }

    private void SelectPath(string path)
    {
        if (_runCommand == null)
        {
            return;
        }
        if (DirectoryTree.Items.Count < 1)
        {
            InitializeTree();
        }
        TreeViewItem current = (TreeViewItem)DirectoryTree.Items[0];
        foreach (string segment in path.Split('/'))
        {
            if (segment == "")
            {
                continue;
            }
            TreeViewItem child = FindChild(current, segment);
            if (child == null)
            {
                return;
            }
            child.IsExpanded = true;
            child.IsSelected = true;
            LoadChildren(child);
            current = child;
        }
        FolderBox.Text = path;
    }

    private static TreeViewItem FindChild(TreeViewItem parent, string name)
    {
        foreach (object item in parent.Items)
        {
            if (item is TreeViewItem child && (string)child.Header == name)
            {
                return child;
            }
        }
        return null;
    }

    private TreeViewItem CreateNode(string name, string path)
    {
        TreeViewItem node = new TreeViewItem { Header = name, Tag = path };
        node.Items.Add(new TreeViewItem { Header = PlaceholderHeader });
        node.Expanded += (object sender, RoutedEventArgs e) => LoadChildren((TreeViewItem)sender);
        return node;
    }

    private static bool IsPlaceholder(object item)
    {
        return item is TreeViewItem node && (string)node.Header == PlaceholderHeader;
    }

    private static string ChildPath(TreeViewItem parent, string name)
    {
        string parentPath = (string)parent.Tag;
        return parentPath == "/" ? "/" + name : parentPath + "/" + name;
    }

    private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DirectoryTree.SelectedItem is TreeViewItem selected)
        {
            FolderBox.Text = (string)selected.Tag;
        }
    }

    private void FolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            SelectPath(FolderBox.Text);
        }
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e)
    {
        HideBusy();
        IsPolling = false;
        _pendingJob?.Dispose();
        _pendingJob = null;
        _loadingNode = null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _pendingJob?.Dispose();
        base.OnClosed(e);
    }
}
