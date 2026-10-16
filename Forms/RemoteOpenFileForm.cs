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
            string text = RemotePathUtils.GetDirectoryName(value);
            if (text != null)
            {
                _initialDirectory = text;
                string text2 = RemotePathUtils.GetFileName(value);
                if (text2 != null)
                {
                    _initialFileName = text2;
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
                    bool flag = (tsControls.Enabled = false);
                    bool flag3 = (button2.Enabled = flag);
                    bool flag5 = (button.Enabled = flag3);
                    bool enabled = (textBox.Enabled = flag5);
                    doubleBufferedListView.Enabled = enabled;
                }
                else
                {
                    iwcProgress.Visible = false;
                    lvFiles.Height += iwcProgress.Height + iwcProgress.Margin.Top;
                    tbFileName.Location = new Point(tbFileName.Location.X, tbFileName.Location.Y + iwcProgress.Height + iwcProgress.Margin.Top);
                    lFileName.Location = new Point(lFileName.Location.X, lFileName.Location.Y + iwcProgress.Height + iwcProgress.Margin.Top);
                    DoubleBufferedListView doubleBufferedListView2 = lvFiles;
                    TextBox textBox2 = tbFileName;
                    Button button3 = btnOpen;
                    Button button4 = btnCancel;
                    bool flag = (tsControls.Enabled = true);
                    bool flag3 = (button4.Enabled = flag);
                    bool flag5 = (button3.Enabled = flag3);
                    bool enabled = (textBox2.Enabled = flag5);
                    doubleBufferedListView2.Enabled = enabled;
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
                List<string> list = remoteCommand.OutputLines;
                if (list != null && list.Count > 0)
                {
                    return list[0];
                }
            }
        }
        return null;
    }

    public static string Browse(RemoteCommandStarter callback, string text = null, string text2 = null, string text3 = null, DialogMode dialogMode = RemoteOpenFileForm.DialogMode.Open, bool flag = false)
    {
        if (text2 == null && text != null)
        {
            text2 = RemotePathUtils.GetDirectoryName(text);
        }
        if (string.IsNullOrEmpty(text2))
        {
            text2 = GetWorkingDirectory(callback);
        }
        RemoteOpenFileForm remoteOpenFileForm = new RemoteOpenFileForm(callback)
        {
            _initialDirectory = text2,
            Text = "Browse File System on " + (text3 ?? "Remote Host"),
            Mode = dialogMode,
            _initialFileName = RemotePathUtils.GetFileName(text),
            _usePrefixedListing = flag
        };
        if (remoteOpenFileForm.ShowModal() != DialogResult.OK)
        {
            return null;
        }
        return remoteOpenFileForm.SelectedPath;
    }

    protected bool TryStatPath(string text, out bool flag)
    {
        flag = false;
        if (!text.StartsWith("/"))
        {
            text = _currentDirectory + "/" + text;
        }
        IRemoteCommand remoteCommand = null;
        _runCommand("ls", "-1dLF \"" + text + "\"", ref remoteCommand);
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
            List<string> list = remoteCommand.OutputLines;
            if (list == null || list.Count != 1)
            {
                return false;
            }
            flag = list[0].EndsWith("/");
            return true;
        }
    }

    public static List<string> SortEntries(List<string> list3, bool flag = false)
    {
        List<string> list = new List<string>();
        List<string> list2 = new List<string>();
        foreach (string item in list3)
        {
            if (flag)
            {
                if (item.StartsWith("d ") || item.StartsWith("ld "))
                {
                    list.Add(item);
                }
                else
                {
                    list2.Add(item);
                }
            }
            else if (item.EndsWith("/"))
            {
                list.Add(item);
            }
            else
            {
                list2.Add(item);
            }
        }
        list.AddRange(list2);
        return list;
    }

    protected void UpdateDirectoryCombo(string text2)
    {
        tscbDir.Items.Clear();
        string text = text2;
        while (text2 != null && text2.Length > 1)
        {
            tscbDir.Items.Add(text2);
            text2 = RemotePathUtils.GetDirectoryName(text2);
        }
        tscbDir.Items.Add("/");
        _updatingDirCombo = true;
        tscbDir.Text = text;
    }

    protected void NavigateTo(string text2 = null)
    {
        if (_runCommand == null)
        {
            return;
        }
        string text;
        if (text2 != null)
        {
            text = text2;
        }
        else if (_initialDirectory != null)
        {
            text = ((_initialDirectory == null) ? "" : _initialDirectory);
        }
        else
        {
            text = "/";
        }
        if (!text.StartsWith("/"))
        {
            text = _currentDirectory + "/" + text;
        }
        _requestedDirectory = text;
        try
        {
            IRemoteCommand remoteCommand = null;
            try
            {
                string arg = "-1ALF";
                if (_usePrefixedListing)
                {
                    arg = "-F";
                }
                _runCommand("ls", $"{arg} \"{text}\"", ref remoteCommand);
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

    protected void PopulateList(List<string> list, string text2)
    {
        if (list == null)
        {
            list = new List<string>();
        }
        if (string.IsNullOrEmpty(text2))
        {
            lblErrorInfo.Visible = false;
        }
        else
        {
            lblErrorInfo.Visible = true;
            lblErrorInfo.Text = text2;
        }
        UpdateDirectoryCombo(_requestedDirectory);
        _currentDirectory = _requestedDirectory;
        lvFiles.Items.Clear();
        if (list == null)
        {
            return;
        }
        list = SortEntries(list, _usePrefixedListing);
        char[] trimChars = new char[5] { '/', '*', '@', '|', '=' };
        Regex regex = new Regex("stat '(.*)' failed:.*");
        Graphics graphics = Graphics.FromHwnd(Handle);
        int num = 100;
        foreach (string item in list)
        {
            int imageIndex = 1;
            string text = item;
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
                text = item.TrimEnd(trimChars);
            }
            else
            {
                switch (item[0])
                {
                case '-':
                    if (item.StartsWith("- "))
                    {
                        text = item.Substring(2);
                        imageIndex = 1;
                    }
                    break;
                case 'd':
                    if (item.StartsWith("d "))
                    {
                        text = item.Substring(2);
                        imageIndex = 0;
                    }
                    break;
                case 'l':
                    if (item.StartsWith("ld "))
                    {
                        text = item.Substring(3);
                        imageIndex = 0;
                    }
                    else if (item.StartsWith("l- "))
                    {
                        text = item.Substring(3);
                        imageIndex = 1;
                    }
                    else if (item.StartsWith("l? "))
                    {
                        text = item.Substring(3);
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
            int num2 = (int)graphics.MeasureString(text, lvFiles.Font).Width;
            num = Math.Max(num, num2);
            lvFiles.Items.Add(text, imageIndex);
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
        lvFiles.Columns[0].Width = num + 20;
    }

    protected void SelectItem(string text)
    {
        if (text == null || lvFiles.Items.Count == 0)
        {
            return;
        }
        foreach (ListViewItem item in lvFiles.Items)
        {
            if (item.Text == text)
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
            int num = _currentDirectory.LastIndexOf('/');
            string text = _currentDirectory.Substring(0, num);
            text = text.TrimEnd('/');
            if (num == 0)
            {
                text = "/";
            }
            _initialFileName = _currentDirectory.Substring(num + 1);
            NavigateTo(text);
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
            string text = _currentDirectory + "/" + lvFiles.SelectedItems[0].Text;
            if (_currentDirectory == "/")
            {
                text = text.Substring(1);
            }
            text = text.TrimEnd('/');
            NavigateTo(text);
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
            string text = _currentDirectory + "/" + lvFiles.SelectedItems[0].Text;
            NavigateTo(text);
        }
    }

    private bool TryAcceptEnteredPath()
    {
        if (!TryStatPath(tbFileName.Text, out var flag))
        {
            if (_mode == DialogMode.Save)
            {
                _enteredPath = tbFileName.Text;
                DialogResult = DialogResult.OK;
                return true;
            }
            return false;
        }
        if (flag)
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
        int num = tsControls.DisplayRectangle.Width;
        foreach (ToolStripItem item in tsControls.Items)
        {
            if (item != tscbDir)
            {
                num -= item.Width;
                num -= item.Margin.Horizontal;
            }
        }
        tscbDir.Width = Math.Max(0, num - tscbDir.Margin.Horizontal);
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
