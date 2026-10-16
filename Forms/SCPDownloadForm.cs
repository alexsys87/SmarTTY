using SmarTTY.IO;
using System;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class SCPDownloadForm : DpiAwareForm
{
    private string _initialDirectory;

    public bool OpenLocalDirAfterDownload
    {
        get
        {
            return checkBox1.Checked;
        }
        set
        {
            checkBox1.Checked = value;
        }
    }

    public string RemoteFile
    {
        get
        {
            return txtRemoteFile.Text;
        }
        set
        {
            txtRemoteFile.Text = value;
        }
    }

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

    public SCPDownloadForm(RemoteCommandStarter callback, string text, bool flag = false)
    {
        InitializeComponent();
        _remoteFileDialog.CommandRequested += callback;
        _initialDirectory = text;
        checkBox1.Visible = flag;
    }

    private void bntBrowse_Click(object sender, EventArgs e)
    {
        try
        {
            _folderBrowserDialog.SelectedPath = LocalDirectory;
        }
        catch
        {
        }
        if (_folderBrowserDialog.ShowDialog() == DialogResult.OK)
        {
            LocalDirectory = _folderBrowserDialog.SelectedPath;
        }
    }

    private void button1_Click(object sender, EventArgs e)
    {
        if (txtRemoteFile.Text == "" && _initialDirectory != null)
        {
            _remoteFileDialog.InitialDirectory = _initialDirectory;
        }
        _remoteFileDialog.FileName = txtRemoteFile.Text;
        string text = _remoteFileDialog.ShowDialog();
        if (text != null)
        {
            txtRemoteFile.Text = text;
        }
    }
}
