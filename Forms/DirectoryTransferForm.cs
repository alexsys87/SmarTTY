using SmarTTY.Ssh;
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

    public DirectoryTransferForm(SshSession sshSession, bool isUpload)
    {
        InitializeComponent();
        FormIcons.Apply(this, "folder-transfer");
        this._session = sshSession;
        _isUpload = isUpload;
        if (isUpload)
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

    private void SwapControls(Control firstControl, Control secondControl)
    {
        Point location = firstControl.Location;
        firstControl.Location = secondControl.Location;
        secondControl.Location = location;
        int tabIndex = firstControl.TabIndex;
        firstControl.TabIndex = secondControl.TabIndex;
        secondControl.TabIndex = tabIndex;
    }

    private void btnDownload_Click(object sender, EventArgs e)
    {
        string remoteDir = txtRemoteDir.Text;
        string localDir = txtLocalDir.Text;
        bool useTar = rbTar.Checked;
        if (string.IsNullOrEmpty(localDir))
        {
            MessageBox.Show("Local directory cannot be empty", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (string.IsNullOrEmpty(remoteDir))
        {
            MessageBox.Show("Remote directory cannot be empty", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        if (_isUpload && !Directory.Exists(localDir))
        {
            MessageBox.Show(localDir + " is not a valid directory", "Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
            return;
        }
        _cancelRequested = false;
        new Thread(() =>
        {
            RunTransfer(remoteDir, localDir, useTar);
        }).Start();
        TextBox textBox = txtLocalDir;
        TextBox textBox2 = txtRemoteDir;
        Button button = btnDownload;
        RadioButton radioButton = rbTar;
        bool disabledState = (rbSCP.Enabled = false);
        bool isEnabled = (radioButton.Enabled = disabledState);
        bool downloadButtonEnabled = (button.Enabled = isEnabled);
        bool enabled = (textBox2.Enabled = downloadButtonEnabled);
        textBox.Enabled = enabled;
        progressBar1.Visible = true;
    }

    private void RunTransfer(string remoteDir, string localDir, bool useTar)
    {
        DirectoryCopier directoryCopier = new DirectoryCopier(this._session);
        directoryCopier.UseTar = useTar;
        directoryCopier.Progress += this.OnProgress;
        directoryCopier.Log += OnLog;
        bool completed = false;
        _errorLog.Length = 0;
        Exception exception = null;
        try
        {
            if (_isUpload)
            {
                directoryCopier.Upload(localDir, remoteDir);
            }
            else
            {
                directoryCopier.Download(remoteDir, localDir);
            }
            completed = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception transferException)
        {
            exception = transferException;
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
                bool enabledState = (rbSCP.Enabled = true);
                bool isEnabled = (radioButton.Enabled = enabledState);
                bool downloadButtonEnabled = (button.Enabled = isEnabled);
                bool enabled = (textBox2.Enabled = downloadButtonEnabled);
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
                if (completed || _closeRequested)
                {
                    Close();
                }
            }));
        }
        catch
        {
        }
    }

    private void OnLog(object sender, string logMessage)
    {
        _errorLog.AppendLine(logMessage);
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

    private void UpdateProgress(DirectoryCopier.CopyStage copyStage, string currentPath, long bytesDone, long bytesTotal)
    {
        if (bytesDone > bytesTotal)
        {
            bytesTotal = bytesDone;
        }
        if (bytesTotal == 0L)
        {
            bytesTotal = 1L;
        }
        switch (copyStage)
        {
            case DirectoryCopier.CopyStage.Counting:
                if (currentPath == null)
                {
                    label1.Text = "Determining directory size...";
                }
                else
                {
                    label1.Text = "Scanning " + currentPath + "...";
                }
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = $"{StringUtils.FormatSize(bytesDone)}B found";
                break;
            case DirectoryCopier.CopyStage.CopyingFile:
                label1.Text = (_isUpload ? "Uploading " : "Downloading ") + currentPath + "...";
                progressBar1.Style = ProgressBarStyle.Blocks;
                progressBar1.Value = (int)(bytesDone * progressBar1.Maximum / bytesTotal);
                lblStats.Text = $"{StringUtils.FormatSize(bytesDone)}B done";
                break;
            case DirectoryCopier.CopyStage.CopyingTarEntry:
                label1.Text = "Linking " + currentPath + "...";
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = "";
                break;
            case DirectoryCopier.CopyStage.StreamingTar:
                label1.Text = "Downloading external symbolic link targets...";
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblStats.Text = $"{StringUtils.FormatSize(bytesDone)}B done";
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
        string dialogResult = _remoteFolderDialog.ShowDialog();
        if (dialogResult != null)
        {
            txtRemoteDir.Text = dialogResult;
        }
    }

    private void StartRemoteCommand(string command, string arguments, ref IRemoteCommand remoteCommand)
    {
        SshShellChannel sshShellChannel = this._session.OpenShellChannel(command + " " + arguments);
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
