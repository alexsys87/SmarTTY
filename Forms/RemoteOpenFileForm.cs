using SmarTTY.Controls;
using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class RemoteOpenFileForm : DpiAwareForm
{
    public enum DialogMode
    {
        Open,
        Save
    }

    private IRemoteCommand _pendingJob;

    protected string _initialDirectory;

    protected string _initialFileName;

    protected string _currentDirectory;

    protected string _requestedDirectory;

    protected string _enteredPath;

    protected DialogMode _mode;

    protected bool _usePrefixedListing;

    private RemoteCommandStarter _runCommand;

    private bool _updatingDirCombo;

    public string SelectedPath
    {
        get
        {
            if (_enteredPath != null)
            {
                if (!_enteredPath.StartsWith("/"))
                {
                    return _currentDirectory.TrimEnd('/') + "/" + _enteredPath;
                }
                return _enteredPath;
            }
            if (lvFiles.SelectedItems == null || lvFiles.SelectedItems.Count == 0)
            {
                return null;
            }
            if (_currentDirectory == "/")
            {
                return lvFiles.SelectedItems[0].Text;
            }
            return _currentDirectory + "/" + lvFiles.SelectedItems[0].Text;
        }
        set
        {
            if (value == null)
            {
                return;
            }
            string dir = RemotePathUtils.GetDirectoryName(value);
            if (dir != null)
            {
                _initialDirectory = dir;
                string fileName = RemotePathUtils.GetFileName(value);
                if (fileName != null)
                {
                    _initialFileName = fileName;
                }
            }
        }
    }

    [DefaultValue(DialogMode.Open)]
    public DialogMode Mode
    {
        get
        {
            return _mode;
        }
        set
        {
            _mode = value;
            if (_mode == DialogMode.Save)
            {
                btnOpen.Text = "&Save";
            }
            else
            {
                btnOpen.Text = "&Open";
            }
        }
    }

    protected bool IsBusy
    {
        set
        {
            if (iwcProgress.Visible != value)
            {
                if (value)
                {
                    iwcProgress.Visible = true;
                    lvFiles.Height -= iwcProgress.Height + iwcProgress.Margin.Top;
                    tbFileName.Location = new Point(tbFileName.Location.X, tbFileName.Location.Y - iwcProgress.Height - iwcProgress.Margin.Top);
                    lFileName.Location = new Point(lFileName.Location.X, lFileName.Location.Y - iwcProgress.Height - iwcProgress.Margin.Top);
                    DoubleBufferedListView doubleBufferedListView = lvFiles;
                    TextBox textBox = tbFileName;
                    Button button = btnOpen;
                    Button button2 = btnCancel;
                    bool disabledState = (tsControls.Enabled = false);
                    bool isEnabled = (button2.Enabled = disabledState);
                    bool openButtonEnabled = (button.Enabled = isEnabled);
                    bool enabled = (textBox.Enabled = openButtonEnabled);
                    doubleBufferedListView.Enabled = enabled;
                }
                else
                {
                    iwcProgress.Visible = false;
                    lvFiles.Height += iwcProgress.Height + iwcProgress.Margin.Top;
                    tbFileName.Location = new Point(tbFileName.Location.X, tbFileName.Location.Y + iwcProgress.Height + iwcProgress.Margin.Top);
                    lFileName.Location = new Point(lFileName.Location.X, lFileName.Location.Y + iwcProgress.Height + iwcProgress.Margin.Top);
                    DoubleBufferedListView fileList = lvFiles;
                    TextBox textBox2 = tbFileName;
                    Button button3 = btnOpen;
                    Button button4 = btnCancel;
                    bool enabledState = (tsControls.Enabled = true);
                    bool cancelButtonEnabled = (button4.Enabled = enabledState);
                    bool openButtonEnabled = (button3.Enabled = cancelButtonEnabled);
                    bool enabled = (textBox2.Enabled = openButtonEnabled);
                    fileList.Enabled = enabled;
                }
            }
        }
    }

    private RemoteOpenFileForm(RemoteCommandStarter callback)
    {
        InitializeComponent();
        _runCommand = callback;
        DpiAwareForm.ConfigureToolStrip(tsControls);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        lvFiles.Focus();
    }

    internal static string GetWorkingDirectory(RemoteCommandStarter callback)
    {
        IRemoteCommand remoteCommand = null;
        callback("pwd", "", ref remoteCommand);
        using (remoteCommand)
        {
            if (remoteCommand.WaitForExit(5000))
            {
                List<string> output = remoteCommand.OutputLines;
                if (output != null && output.Count > 0)
                {
                    return output[0];
                }
            }
        }
        return null;
    }

    public static string Browse(RemoteCommandStarter callback, string filePath = null, string dirPath = null, string hostDisplayName = null, DialogMode dialogMode = RemoteOpenFileForm.DialogMode.Open, bool usePrefixedListing = false)
    {
        if (dirPath == null && filePath != null)
        {
            dirPath = RemotePathUtils.GetDirectoryName(filePath);
        }
        if (string.IsNullOrEmpty(dirPath))
        {
            dirPath = GetWorkingDirectory(callback);
        }
        RemoteOpenFileForm remoteOpenFileForm = new RemoteOpenFileForm(callback)
        {
            _initialDirectory = dirPath,
            Text = "Browse File System on " + (hostDisplayName ?? "Remote Host"),
            Mode = dialogMode,
            _initialFileName = RemotePathUtils.GetFileName(filePath),
            _usePrefixedListing = usePrefixedListing
        };
        if (remoteOpenFileForm.ShowModal() != DialogResult.OK)
        {
            return null;
        }
        return remoteOpenFileForm.SelectedPath;
    }

    protected bool TryStatPath(string path, out bool isDirectory)
    {
        isDirectory = false;
        if (!path.StartsWith("/"))
        {
            path = _currentDirectory + "/" + path;
        }
        IRemoteCommand remoteCommand = null;
        _runCommand("ls", "-1dLF \"" + path + "\"", ref remoteCommand);
        if (remoteCommand == null)
        {
            return false;
        }
        using (remoteCommand)
        {
            remoteCommand.WaitForExit(-1);
            if (remoteCommand.ExitCode != 0)
            {
                return false;
            }
            List<string> output = remoteCommand.OutputLines;
            if (output == null || output.Count != 1)
            {
                return false;
            }
            isDirectory = output[0].EndsWith("/");
            return true;
        }
    }

    public static List<string> SortEntries(List<string> entries, bool isPrefixed = false)
    {
        List<string> dirList = new List<string>();
        List<string> fileList = new List<string>();
        foreach (string item in entries)
        {
            if (isPrefixed)
            {
                if (item.StartsWith("d ") || item.StartsWith("ld "))
                {
                    dirList.Add(item);
                }
                else
                {
                    fileList.Add(item);
                }
            }
            else if (item.EndsWith("/"))
            {
                dirList.Add(item);
            }
            else
            {
                fileList.Add(item);
            }
        }
        dirList.AddRange(fileList);
        return dirList;
    }

    protected void UpdateDirectoryCombo(string dirPath)
    {
        tscbDir.Items.Clear();
        string current = dirPath;
        while (dirPath != null && dirPath.Length > 1)
        {
            tscbDir.Items.Add(dirPath);
            dirPath = RemotePathUtils.GetDirectoryName(dirPath);
        }
        tscbDir.Items.Add("/");
        _updatingDirCombo = true;
        tscbDir.Text = current;
    }

    protected void NavigateTo(string dirPath = null)
    {
        if (_runCommand == null)
        {
            return;
        }
        string targetDir;
        if (dirPath != null)
        {
            targetDir = dirPath;
        }
        else if (_initialDirectory != null)
        {
            targetDir = ((_initialDirectory == null) ? "" : _initialDirectory);
        }
        else
        {
            targetDir = "/";
        }
        if (!targetDir.StartsWith("/"))
        {
            targetDir = _currentDirectory + "/" + targetDir;
        }
        _requestedDirectory = targetDir;
        try
        {
            IRemoteCommand remoteCommand = null;
            try
            {
                string lsFlags = "-1ALF";
                if (_usePrefixedListing)
                {
                    lsFlags = "-F";
                }
                _runCommand("ls", $"{lsFlags} \"{targetDir}\"", ref remoteCommand);
                if (remoteCommand == null)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
            if (remoteCommand.WaitForExit(300))
            {
                using (remoteCommand)
                {
                    if (remoteCommand.ExitCode == 0 || (remoteCommand.OutputLines != null && remoteCommand.OutputLines.Count != 0))
                    {
                        PopulateList(remoteCommand.OutputLines, remoteCommand.ErrorText);
                    }
                    else
                    {
                        _timer.Enabled = false;
                        string value = remoteCommand.ErrorText;
                        if (string.IsNullOrEmpty(value))
                        {
                            MessageBox.Show("'ls -1' failed with code: " + remoteCommand.ExitCode + ". Non-existent directory?", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                        else
                        {
                            MessageBox.Show(value, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        }
                        _pendingJob = null;
                        _requestedDirectory = null;
                        if (_currentDirectory == null)
                        {
                            NavigateTo("/");
                        }
                    }
                    return;
                }
            }
            IsBusy = true;
            if (_pendingJob != null)
            {
                _pendingJob.Dispose();
            }
            _pendingJob = remoteCommand;
            _timer.Enabled = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            IsBusy = false;
            _pendingJob = null;
            _timer.Enabled = false;
            _requestedDirectory = null;
        }
    }

    private void OnPollTimer(object sender, EventArgs e)
    {
        if (_pendingJob == null)
        {
            _timer.Enabled = false;
            _requestedDirectory = null;
            IsBusy = false;
        }
        else if (_pendingJob.WaitForExit(0))
        {
            IsBusy = false;
            if (_pendingJob.ExitCode == 0 || _pendingJob.OutputLines.Count != 0)
            {
                _timer.Enabled = false;
                PopulateList(_pendingJob.OutputLines, _pendingJob.ErrorText);
                _requestedDirectory = null;
            }
            else
            {
                _timer.Enabled = false;
                MessageBox.Show("Command returned with code: " + _pendingJob.ExitCode, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                _requestedDirectory = null;
            }
            _pendingJob.Dispose();
            _pendingJob = null;
        }
    }

    protected void PopulateList(List<string> lines, string errorText)
    {
        if (lines == null)
        {
            lines = new List<string>();
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
        UpdateDirectoryCombo(_requestedDirectory);
        _currentDirectory = _requestedDirectory;
        lvFiles.Items.Clear();
        if (lines == null)
        {
            return;
        }
        lines = SortEntries(lines, _usePrefixedListing);
        char[] trimChars = new char[5] { '/', '*', '@', '|', '=' };
        Regex regex = new Regex("stat '(.*)' failed:.*");
        Graphics graphics = Graphics.FromHwnd(Handle);
        int maxItemWidth = 100;
        foreach (string item in lines)
        {
            int imageIndex = 1;
            string entryName = item;
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }
            if (!_usePrefixedListing)
            {
                switch (item[item.Length - 1])
                {
                case '/':
                    imageIndex = 0;
                    break;
                case '*':
                    imageIndex = 2;
                    break;
                case '=':
                case '@':
                case '|':
                    imageIndex = 3;
                    break;
                }
                entryName = item.TrimEnd(trimChars);
            }
            else
            {
                switch (item[0])
                {
                case '-':
                    if (item.StartsWith("- "))
                    {
                        entryName = item.Substring(2);
                        imageIndex = 1;
                    }
                    break;
                case 'd':
                    if (item.StartsWith("d "))
                    {
                        entryName = item.Substring(2);
                        imageIndex = 0;
                    }
                    break;
                case 'l':
                    if (item.StartsWith("ld "))
                    {
                        entryName = item.Substring(3);
                        imageIndex = 0;
                    }
                    else if (item.StartsWith("l- "))
                    {
                        entryName = item.Substring(3);
                        imageIndex = 1;
                    }
                    else if (item.StartsWith("l? "))
                    {
                        entryName = item.Substring(3);
                        imageIndex = 1;
                    }
                    break;
                case 's':
                    if (regex.Match(item).Success)
                    {
                        continue;
                    }
                    break;
                }
            }
            int itemWidth = (int)graphics.MeasureString(entryName, lvFiles.Font).Width;
            maxItemWidth = Math.Max(maxItemWidth, itemWidth);
            lvFiles.Items.Add(entryName, imageIndex);
        }
        if (_initialFileName != null)
        {
            SelectItem(_initialFileName);
            _initialFileName = null;
        }
        else if (lvFiles.Items.Count > 0)
        {
            lvFiles.Items[0].Focused = true;
        }
        lvFiles.Columns[0].Width = maxItemWidth + 20;
    }

    protected void SelectItem(string itemName)
    {
        if (itemName == null || lvFiles.Items.Count == 0)
        {
            return;
        }
        foreach (ListViewItem item in lvFiles.Items)
        {
            if (item.Text == itemName)
            {
                item.Focused = true;
                item.Selected = true;
                lvFiles.EnsureVisible(item.Index);
                break;
            }
        }
    }

    private void RemoteOpenFileForm_Load(object sender, EventArgs e)
    {
        NavigateTo();
    }

    private void tspUp_Click(object sender, EventArgs e)
    {
        if (_currentDirectory != null && _currentDirectory.Length > 1)
        {
            int slashIndex = _currentDirectory.LastIndexOf('/');
            string parentPath = _currentDirectory.Substring(0, slashIndex);
            parentPath = parentPath.TrimEnd('/');
            if (slashIndex == 0)
            {
                parentPath = "/";
            }
            _initialFileName = _currentDirectory.Substring(slashIndex + 1);
            NavigateTo(parentPath);
        }
    }

    private void lvFiles_DoubleClick(object sender, EventArgs e)
    {
        if (lvFiles.SelectedItems == null || lvFiles.SelectedItems.Count != 1 || lvFiles.SelectedItems[0].ImageIndex != 0)
        {
            btnOpen_Click(sender, e);
        }
        else if (_currentDirectory != null)
        {
            string childPath = _currentDirectory + "/" + lvFiles.SelectedItems[0].Text;
            if (_currentDirectory == "/")
            {
                childPath = childPath.Substring(1);
            }
            childPath = childPath.TrimEnd('/');
            NavigateTo(childPath);
        }
    }

    private void lvFiles_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (lvFiles.SelectedItems.Count == 1 && lvFiles.SelectedItems[0].ImageIndex != 0)
        {
            tbFileName.Text = lvFiles.SelectedItems[0].Text;
        }
    }

    private void btnOpen_Click(object sender, EventArgs e)
    {
        if ((!(tbFileName.Text != "") || !TryAcceptEnteredPath()) && lvFiles.SelectedItems != null && lvFiles.SelectedItems.Count != 0)
        {
            if (lvFiles.SelectedItems[0].ImageIndex != 0)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            string childPath = _currentDirectory + "/" + lvFiles.SelectedItems[0].Text;
            NavigateTo(childPath);
        }
    }

    private bool TryAcceptEnteredPath()
    {
        if (!TryStatPath(tbFileName.Text, out var isDirectory))
        {
            if (_mode == DialogMode.Save)
            {
                _enteredPath = tbFileName.Text;
                DialogResult = DialogResult.OK;
                return true;
            }
            return false;
        }
        if (isDirectory)
        {
            NavigateTo(tbFileName.Text);
            tbFileName.Text = "";
            DialogResult = DialogResult.None;
        }
        else
        {
            _enteredPath = tbFileName.Text;
            DialogResult = DialogResult.OK;
        }
        return true;
    }

    private void tscbDir_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            if (tscbDir.Text.Length > 1 && tscbDir.Text.EndsWith("/"))
            {
                tscbDir.Text = tscbDir.Text.TrimEnd('/');
            }
            NavigateTo(tscbDir.Text);
            tscbDir.SelectionStart = tscbDir.Text.Length;
            tscbDir.SelectionLength = 0;
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Return)
        {
            if (ActiveControl == tscbDir.Control)
            {
                return false;
            }
            if (ActiveControl == lvFiles)
            {
                return false;
            }
        }
        return base.ProcessDialogKey(keyData);
    }

    private void tbFileName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Return)
        {
            if (!TryAcceptEnteredPath())
            {
                MessageBox.Show(tbFileName.Text + " does not exist", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            }
            NavigateTo(tbFileName.Text);
        }
    }

    private void tscbDir_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_updatingDirCombo)
        {
            _updatingDirCombo = false;
        }
        else if (tscbDir.SelectedIndex >= 0)
        {
            NavigateTo(tscbDir.SelectedItem.ToString());
        }
    }

    private void tsControls_Layout(object sender, LayoutEventArgs e)
    {
        int availableWidth = tsControls.DisplayRectangle.Width;
        foreach (ToolStripItem item in tsControls.Items)
        {
            if (item != tscbDir)
            {
                availableWidth -= item.Width;
                availableWidth -= item.Margin.Horizontal;
            }
        }
        tscbDir.Width = Math.Max(0, availableWidth - tscbDir.Margin.Horizontal);
    }

    private void RemoteOpenFileForm_Resize(object sender, EventArgs e)
    {
        tsControls.PerformLayout();
    }

    private void lvFiles_KeyPress(object sender, KeyPressEventArgs e)
    {
        if (e.KeyChar == '\r')
        {
            lvFiles_DoubleClick(sender, e);
        }
        else if (e.KeyChar == '\b')
        {
            tspUp_Click(sender, e);
        }
    }

    private void OnCancelBusy(object sender, EventArgs e)
    {
        _timer.Enabled = false;
        IsBusy = false;
        _requestedDirectory = null;
        _pendingJob = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_pendingJob != null)
        {
            _pendingJob.Dispose();
        }
    }
}
