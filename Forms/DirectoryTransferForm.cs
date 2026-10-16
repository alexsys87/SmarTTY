using ManagedLibSSH2;
using SmarTTY.IO;
using SmarTTY.Sync;
using SmarTTY.Utilities;
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class DirectoryTransferForm : DpiAwareForm
{

    private SshSession _session;

    private bool _isUpload;

    private StringBuilder _errorLog = new StringBuilder();

    private DateTime _lastProgressUpdate;

    private string _currentPath;

    private bool _cancelRequested;

    private bool _closeRequested;

    public string LocalDirectory
    {
        get
        {
            return txtLocalDir.Text;
        }
        set
        {
            txtLocalDir.Text = value;
        }
    }

    public string RemoteDirectory
    {
        get
        {
            return txtRemoteDir.Text;
        }
        set
        {
            txtRemoteDir.Text = value;
        }
    }

    public bool IsUpload => _isUpload;

    public DirectoryTransferForm(SshSession sshSession, bool flag)
    {
        InitializeComponent();
        this._session = sshSession;
        _isUpload = flag;
        if (flag)
        {
            pictureBox1.Visible = false;
            pictureBox2.Visible = true;
            SwapControls(lblRemoteFile, lblLocalDir);
            SwapControls(txtRemoteDir, txtLocalDir);
            SwapControls(btnRemoteBrowse, bntBrowse);
            btnDownload.Text = "Upload";
            Text = "Upload a directory";
        }
        else
        {
            Text = "Download a directory";
        }
    }

    private void SwapControls(Control control, Control control2)
    {
        Point location = control.Location;
        control.Location = control2.Location;
        control2.Location = location;
        int num = control.TabIndex;
        control.TabIndex = control2.TabIndex;
        control2.TabIndex = num;
    }

    private void btnDownload_Click(object sender, EventArgs e)
    {
        string text = txtRemoteDir.Text;
        string text2 = txtLocalDir.Text;
        bool flag2 = rbTar.Checked;
        if (string.IsNullOrEmpty(text2))
        {
            MessageBox.Show("Local directory cannot be empty", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show("Remote directory cannot be empty", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (_isUpload && !Directory.Exists(text2))
        {
            MessageBox.Show(text2 + " is not a valid directory", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        _cancelRequested = false;
        new Thread(() =>
        {
            RunTransfer(text, text2, flag2);
        }).Start();
        TextBox textBox = txtLocalDir;
        TextBox textBox2 = txtRemoteDir;
        Button button = btnDownload;
        RadioButton radioButton = rbTar;
        bool flag = (rbSCP.Enabled = false);
        bool flag3 = (radioButton.Enabled = flag);
        bool flag5 = (button.Enabled = flag3);
        bool enabled = (textBox2.Enabled = flag5);
        textBox.Enabled = enabled;
        progressBar1.Visible = true;
    }

    private void RunTransfer(string text, string text2, bool flag2)
    {
        DirectoryCopier directoryCopier = new DirectoryCopier(this._session);
        directoryCopier.UseTar = flag2;
        directoryCopier.Progress += this.OnProgress;
        directoryCopier.Log += OnLog;
        bool flag4 = false;
        _errorLog.Length = 0;
        Exception exception = null;
        try
        {
            if (_isUpload)
            {
                directoryCopier.Upload(text2, text);
            }
            else
            {
                directoryCopier.Download(text, text2);
            }
            flag4 = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex2)
        {
            exception = ex2;
        }
        try
        {
            BeginInvoke((ThreadStart)(() =>
            {
                label1.Text = "Inactive";
                lblStats.Text = "";
                TextBox textBox = txtLocalDir;
                TextBox textBox2 = txtRemoteDir;
                Button button = btnDownload;
                RadioButton radioButton = rbTar;
                bool flag = (rbSCP.Enabled = true);
                bool flag3 = (radioButton.Enabled = flag);
                bool flag5 = (button.Enabled = flag3);
                bool enabled = (textBox2.Enabled = flag5);
                textBox.Enabled = enabled;
                progressBar1.Visible = false;
                if (_errorLog.Length != 0)
                {
                    new TransferErrorForm(exception, _errorLog.ToString()).ShowModal();
                }
                else if (exception != null)
                {
                    MessageBox.Show("Cannot transfer a directory: " + exception.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                }
                if (flag4 || _closeRequested)
                {
                    Close();
                }
            }));
        }
        catch
        {
        }
    }

    private void OnLog(object obj, string text)
    {
        _errorLog.AppendLine(text);
    }

    private void OnProgress(DirectoryCopier.CopyProgress copyProgress)
    {
        if (_cancelRequested)
        {
            throw new OperationCanceledException();
        }
        if (copyProgress.Path != null)
        {
            _currentPath = copyProgress.Path;
        }
        if (copyProgress.Stage != DirectoryCopier.CopyStage.CopyingFile || !((DateTime.Now - _lastProgressUpdate).TotalMilliseconds < 100.0))
        {
            _lastProgressUpdate = DateTime.Now;
            BeginInvoke((ThreadStart)(() =>
            {
                UpdateProgress(copyProgress.Stage, _currentPath, copyProgress.BytesDone, copyProgress.BytesTotal);
            }));
        }
    }

    private void UpdateProgress(DirectoryCopier.CopyStage copyStage, string text, long value2, long value3)
    {
        if (value2 > value3)
        {
            value3 = value2;
        }
        if (value3 == 0L)
        {
            value3 = 1L;
        }
        switch (copyStage)
        {
            case DirectoryCopier.CopyStage.Counting:
                if (text == null)
                {
                    label1.Text = "Determining directory size...";
                }
                else
                {
                    label1.Text = "Scanning " + text + "...";
                }
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = $"{StringUtils.FormatSize(value2)}B found";
                break;
            case DirectoryCopier.CopyStage.CopyingFile:
                label1.Text = (_isUpload ? "Uploading " : "Downloading ") + text + "...";
                progressBar1.Style = ProgressBarStyle.Blocks;
                progressBar1.Value = (int)(value2 * progressBar1.Maximum / value3);
                lblStats.Text = $"{StringUtils.FormatSize(value2)}B done";
                break;
            case DirectoryCopier.CopyStage.CopyingTarEntry:
                label1.Text = "Linking " + text + "...";
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = "";
                break;
            case DirectoryCopier.CopyStage.StreamingTar:
                label1.Text = "Downloading external symbolic link targets...";
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = $"{StringUtils.FormatSize(value2)}B done";
                break;
        }
    }

    private void btnCancel_Click(object sender, EventArgs e)
    {
        _cancelRequested = true;
        if (btnDownload.Enabled)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!btnDownload.Enabled)
        {
            _cancelRequested = true;
            _closeRequested = true;
            e.Cancel = true;
        }
    }

    private void btnRemoteBrowse_Click(object sender, EventArgs e)
    {
        _remoteFolderDialog.InitialFolder = txtRemoteDir.Text;
        _remoteFolderDialog.HostName = this._session.HostName;
        string text = _remoteFolderDialog.ShowDialog();
        if (text != null)
        {
            txtRemoteDir.Text = text;
        }
    }

    private void StartRemoteCommand(string text, string text2, ref IRemoteCommand remoteCommand)
    {
        SshShellChannel sshShellChannel = this._session.OpenShellChannel(text + " " + text2);
        if (sshShellChannel != null)
        {
            remoteCommand = new RemoteCommandJob(sshShellChannel);
        }
    }

    private void bntBrowse_Click(object sender, EventArgs e)
    {
        if (_folderBrowserDialog.ShowDialog() == DialogResult.OK)
        {
            txtLocalDir.Text = _folderBrowserDialog.SelectedPath;
        }
    }
}
