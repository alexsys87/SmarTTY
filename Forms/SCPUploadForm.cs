using SmarTTY.IO;
using System;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class SCPUploadForm : DpiAwareForm
{

    public string LocalFile
    {
        get
        {
            return txtLocalFile.Text;
        }
        set
        {
            txtLocalFile.Text = value;
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

    public SCPUploadForm(RemoteCommandStarter callback)
    {
        InitializeComponent();
        _remoteFolderDialog.CommandRequested += callback;
    }

    private void btnOpen_Click(object sender, EventArgs e)
    {
        if (_openFileDialog.ShowDialog() == DialogResult.OK)
        {
            this.LocalFile = _openFileDialog.FileName;
        }
    }

    private void bntBrowse_Click(object sender, EventArgs e)
    {
        _remoteFolderDialog.InitialFolder = txtRemoteDir.Text;
        string text = _remoteFolderDialog.ShowDialog();
        if (text != null)
        {
            txtRemoteDir.Text = text;
        }
    }
}
