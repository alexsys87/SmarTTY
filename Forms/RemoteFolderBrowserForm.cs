using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class RemoteFolderBrowserForm : DpiAwareForm
{
    public class FolderBrowserOptions
    {
        public bool RelativePath;
    }

    protected readonly bool _usePrefixedListing;

    private RemoteCommandStarter _runCommand;

    protected IRemoteCommand _pendingJob;

    protected string _initialPath;

    protected TreeNode _loadingNode;

    public string SelectedPath => txtFolder.Text;

    protected string SelectedNodePath
    {
        get
        {
            if (tvDirectories.SelectedNode == null)
            {
                return null;
            }
            return tvDirectories.SelectedNode.FullPath.Substring(1);
        }
    }

    private bool IsPolling
    {
        set
        {
            _timer.Enabled = value;
        }
    }

    private RemoteFolderBrowserForm(bool usePrefixedListing)
    {
        InitializeComponent();
        FormIcons.Apply(this, "remote-folder");
        _usePrefixedListing = usePrefixedListing;
    }

    public static string Browse(RemoteCommandStarter callback, bool usePrefixedListing, string initialPath = null, string hostDisplayName = null, FolderBrowserOptions folderBrowserOptions = null)
    {
        if (string.IsNullOrEmpty(initialPath))
        {
            initialPath = RemoteOpenFileForm.GetWorkingDirectory(callback);
        }
        RemoteFolderBrowserForm remoteFolderBrowserForm = new RemoteFolderBrowserForm(usePrefixedListing)
        {
            _runCommand = callback,
            _initialPath = initialPath,
            Text = "Select a Folder on " + (hostDisplayName ?? "remote host")
        };
        if (folderBrowserOptions != null)
        {
            remoteFolderBrowserForm.cbRelative.Visible = true;
            remoteFolderBrowserForm.cbRelative.Checked = folderBrowserOptions.RelativePath;
        }
        else
        {
            remoteFolderBrowserForm.cbRelative.Visible = false;
        }
        if (remoteFolderBrowserForm.ShowModal() != DialogResult.OK)
        {
            return null;
        }
        return remoteFolderBrowserForm.SelectedPath;
    }

    protected void ShowBusy()
    {
        if (!iwcProgress.Visible)
        {
            iwcProgress.Visible = true;
            tvDirectories.Height -= iwcProgress.Height + iwcProgress.Margin.Top;
            TreeView treeView = tvDirectories;
            Button button = bOK;
            bool enabledState = (bCancel.Enabled = false);
            bool enabled = (button.Enabled = enabledState);
            treeView.Enabled = enabled;
        }
    }

    protected void HideBusy()
    {
        if (iwcProgress.Visible)
        {
            iwcProgress.Visible = false;
            tvDirectories.Height += iwcProgress.Height + iwcProgress.Margin.Top;
            TreeView treeView = tvDirectories;
            Button button = bOK;
            bool enabledState = (bCancel.Enabled = true);
            bool enabled = (button.Enabled = enabledState);
            treeView.Enabled = enabled;
        }
    }

    protected void InitializeTree(bool clearNodes = false)
    {
        TreeNode treeNode = tvDirectories.Nodes.Add("/", "/", 0);
        treeNode.Nodes.Add("", "");
        treeNode.Expand();
        LoadChildren(treeNode, clearNodes: true);
    }

    protected void LoadChildren(TreeNode treeNode, bool clearNodes = false)
    {
        if (treeNode == null || this._runCommand == null || treeNode.Nodes.Count != 1 || !(treeNode.Nodes[0].Text == ""))
        {
            return;
        }
        if (clearNodes)
        {
            treeNode.Nodes.Clear();
        }
        string fullPath = treeNode.FullPath;
        fullPath = "/" + fullPath.TrimStart('/');
        _loadingNode = treeNode;
        try
        {
            IRemoteCommand remoteCommand = null;
            string lsFlags = "-1pLA";
            if (_usePrefixedListing)
            {
                lsFlags = "-F";
            }
            this._runCommand("ls", lsFlags + " \"" + fullPath + "\"", ref remoteCommand);
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
                        string value = remoteCommand.ErrorText;
                        if (string.IsNullOrEmpty(value))
                        {
                            // Exit status -1 is what a channel closed by a lost connection reports: no verdict on the directory.
                            MessageBox.Show(remoteCommand.ExitCode == -1 ? "The listing was interrupted, most likely because the connection to the server was lost. Try again after reconnecting." : "'ls -1' failed with code: " + remoteCommand.ExitCode + ". Non-existent directory?", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                        else
                        {
                            MessageBox.Show(value, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
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
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
                MessageBox.Show("Command returned with code: " + _pendingJob.ExitCode, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                _loadingNode = null;
            }
            _pendingJob.Dispose();
            _pendingJob = null;
        }
    }

    protected void PopulateChildren(List<string> lines, string errorText)
    {
        if (_loadingNode == null)
        {
            return;
        }
        if (_loadingNode.Nodes.Count == 1 && _loadingNode.Nodes[0].Text == "")
        {
            _loadingNode.Nodes.Clear();
        }
        if (string.IsNullOrEmpty(errorText))
        {
            lblErrorInfo.Visible = false;
        }
        else
        {
            lblErrorInfo.Visible = true;
            lblErrorInfo.Text = errorText;
        }
        if (lines == null)
        {
            return;
        }
        if (_usePrefixedListing)
        {
            lines = RemoteOpenFileForm.SortEntries(lines, isPrefixed: true);
        }
        foreach (string item in lines)
        {
            if (_usePrefixedListing)
            {
                if (item.StartsWith("d "))
                {
                    _loadingNode.Nodes.Add(item.Substring(2, item.Length - 2), item.Substring(2, item.Length - 2)).Nodes.Add("", "");
                }
            }
            else if (item.EndsWith("/"))
            {
                _loadingNode.Nodes.Add(item.Substring(0, item.Length - 1), item.Substring(0, item.Length - 1)).Nodes.Add("", "");
            }
        }
    }

    protected void SelectPath(string path)
    {
        if (this._runCommand == null)
        {
            return;
        }
        if (tvDirectories.Nodes.Count < 1)
        {
            InitializeTree();
        }
        string[] segments = path.Split('/');
        TreeNodeCollection nodes = tvDirectories.Nodes[0].Nodes;
        foreach (string segment in segments)
        {
            if (!(segment == ""))
            {
                int nodeIndex = nodes.IndexOfKey(segment);
                if (nodeIndex < 0)
                {
                    return;
                }
                nodes[nodeIndex].Expand();
                tvDirectories.SelectedNode = nodes[nodeIndex];
                LoadChildren(nodes[nodeIndex]);
                nodes = nodes[nodeIndex].Nodes;
            }
        }
        txtFolder.Text = path;
    }

    private void RemoteFolderBrowserForm_Load(object sender, EventArgs e)
    {
        InitializeTree();
        if (_initialPath != null && _initialPath != "")
        {
            SelectPath(_initialPath);
        }
    }

    private void tvDirectories_BeforeExpand(object sender, TreeViewCancelEventArgs e)
    {
        LoadChildren(e.Node);
    }

    private void OnCancelBusy(object sender, EventArgs e)
    {
        HideBusy();
        IsPolling = false;
        if (_pendingJob != null)
        {
            _pendingJob.Dispose();
        }
        _pendingJob = null;
        _loadingNode = null;
    }

    private void txtFolder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            SelectPath(txtFolder.Text);
        }
    }

    private void tvDirectories_AfterSelect(object sender, TreeViewEventArgs e)
    {
        txtFolder.Text = SelectedNodePath;
    }
}
