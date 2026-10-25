using Microsoft.Win32;
using SmarTTY.Forms;
using SmarTTY.IO;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// SCP -&gt; Upload a file: picks one or more local files and the remote directory to put them in.
/// </summary>
public partial class SCPUploadWindow : Window
{
    // The files picked in the open dialog when it returned several; they apply as long as the text box still shows
    // the summary written for them.
    private string[] _selectedFiles;

    private string _selectionSummary;

    private readonly RemoteFolderDialog _remoteFolderDialog;

    /// <summary>The files to upload: those picked in the open dialog, or the single path typed into the text box.</summary>
    public string[] LocalFiles
    {
        get
        {
            if (_selectedFiles != null && LocalFileBox.Text == _selectionSummary)
            {
                return _selectedFiles;
            }
            string path = LocalFileBox.Text.Trim().Trim('"');
            return (path.Length == 0) ? new string[0] : new[] { path };
        }
    }

    public string RemoteDirectory
    {
        get
        {
            return RemoteDirectoryBox.Text;
        }
        set
        {
            RemoteDirectoryBox.Text = value;
        }
    }

    public SCPUploadWindow(RemoteCommandStarter callback)
    {
        InitializeComponent();
        // The remote folder picker is still the WinForms one; it is migrated with the directory-transfer cluster.
        _remoteFolderDialog = new RemoteFolderDialog();
        _remoteFolderDialog.CommandRequested += callback;
        Closed += (object sender, EventArgs e) => _remoteFolderDialog.Dispose();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Title = "Choose the local files to upload",
            Filter = "All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        string[] files = dialog.FileNames;
        if (files.Length > 1)
        {
            _selectedFiles = files;
            _selectionSummary = $"{files.Length} files in {Path.GetDirectoryName(files[0])}: " + string.Join(", ", files.Select(Path.GetFileName));
            LocalFileBox.Text = _selectionSummary;
        }
        else
        {
            _selectedFiles = null;
            LocalFileBox.Text = dialog.FileName;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        _remoteFolderDialog.InitialFolder = RemoteDirectoryBox.Text;
        string selected = _remoteFolderDialog.ShowDialog();
        if (selected != null)
        {
            RemoteDirectoryBox.Text = selected;
        }
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
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
        else if (string.IsNullOrWhiteSpace(RemoteDirectoryBox.Text))
        {
            error = "Enter the remote directory.";
        }
        if (error != null)
        {
            MessageBoxes.Warning(error);
            return;
        }
        DialogResult = true;
    }
}
