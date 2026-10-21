using SmarTTY.IO;
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SmarTTY.Forms;

public partial class SCPUploadForm : DpiAwareForm
{
    // The files picked in the open dialog when it returned several; they apply as long as the text box still shows
    // the summary written for them.
    private string[] _selectedFiles;

    private string _selectionSummary;

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

    /// <summary>
    /// The files to upload: those picked in the open dialog, or the single path typed into the text box.
    /// </summary>
    public string[] LocalFiles
    {
        get
        {
            if (_selectedFiles != null && txtLocalFile.Text == _selectionSummary)
            {
                return _selectedFiles;
            }
            string path = txtLocalFile.Text.Trim().Trim('"');
            return (path.Length == 0) ? new string[0] : new[] { path };
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
        _openFileDialog.Multiselect = true;
    }

    private void btnOpen_Click(object sender, EventArgs e)
    {
        if (_openFileDialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }
        string[] files = _openFileDialog.FileNames;
        if (files.Length > 1)
        {
            _selectedFiles = files;
            _selectionSummary = $"{files.Length} files in {Path.GetDirectoryName(files[0])}: " + string.Join(", ", files.Select(Path.GetFileName));
            LocalFile = _selectionSummary;
        }
        else
        {
            _selectedFiles = null;
            LocalFile = _openFileDialog.FileName;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (DialogResult != DialogResult.OK || e.Cancel)
        {
            return;
        }
        // Checked here, while the user can still correct the input, rather than when the transfer is queued.
        string[] files = LocalFiles;
        string missing = files.FirstOrDefault((string file) => !File.Exists(file));
        string error = null;
        if (files.Length == 0)
        {
            error = "Select a file to upload.";
        }
        else if (missing != null)
        {
            error = Directory.Exists(missing) ? missing + " is a directory. Use SCP -> Upload a directory for folders." : missing + " does not exist.";
        }
        else if (string.IsNullOrWhiteSpace(RemoteDirectory))
        {
            error = "Enter the remote directory.";
        }
        if (error != null)
        {
            MessageBox.Show(error, "SmarTTY", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            e.Cancel = true;
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
