using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// The window of the "Install from URL..." button of the package manager: the user pastes links of package files, they are
/// downloaded by this computer (with a progress bar and Cancel), and the package manager window then hands them to the same
/// upload and installation as the files picked on the disk. The server only gets the files over SSH, so it needs no internet.
/// </summary>
public partial class PackageDownloadWindow : Window
{
    private readonly ILocalPackageFileInstaller _Installer;

    private CancellationTokenSource _Cancellation;

    // Set when the user closes the window during a download: it closes once the download has stopped and its files are removed.
    private bool _CloseWhenStopped;

    private int _LastReport;

    public PackageDownloadWindow(ILocalPackageFileInstaller installer)
    {
        InitializeComponent();
        _Installer = installer ?? throw new ArgumentNullException(nameof(installer));
        PromptText.Text = "Links to the package files to install with " + installer.InstallToolName + " (one per line):";
        HintText.Text = "This computer downloads the files and uploads them to the server over SFTP or SCP, so the server itself does not need "
            + "access to the internet. Accepted: " + PackageUrlDownload.DescribeFilter(installer.LocalPackageFileFilter) + ". If the server cannot "
            + "reach its repositories, add the links of the packages this one depends on as well: all of them are installed together.";
        string fromClipboard = UrlsFromClipboard();
        if (fromClipboard != null)
        {
            UrlBox.Text = fromClipboard;
            UrlBox.CaretIndex = UrlBox.Text.Length;
        }
    }

    /// <summary>The downloaded files (full paths on this computer) once the window closed with true.</summary>
    public string[] DownloadedFiles { get; private set; }

    /// <summary>The folder of this computer that holds <see cref="DownloadedFiles"/>; the caller removes it when they have been uploaded.</summary>
    public string DownloadDirectory { get; private set; }

    private bool IsBusy => _Cancellation != null;

    // The links that are on the clipboard when the window opens (copied from a browser just before), or null.
    private static string UrlsFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                return null;
            }
            string[] urls = PackageUrlDownload.SplitUrls(Clipboard.GetText());
            if (urls.Length == 0 || urls.Length > PackageUrlDownload.MaxUrls || urls.Any(url => PackageUrlDownload.ParseUrl(url, out _) == null))
            {
                return null;
            }
            return string.Join(Environment.NewLine, urls) + Environment.NewLine;
        }
        catch
        {
            // Another program holds the clipboard: the box stays empty.
            return null;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }
        string[] texts = PackageUrlDownload.SplitUrls(UrlBox.Text);
        if (texts.Length == 0)
        {
            MessageBoxes.Error("Paste the link of a package file (http:// or https://).");
            return;
        }
        if (texts.Length > PackageUrlDownload.MaxUrls)
        {
            MessageBoxes.Error("There are " + texts.Length + " links; at most " + PackageUrlDownload.MaxUrls + " are downloaded in one go.");
            return;
        }
        List<Uri> urls = new List<Uri>();
        foreach (string text in texts)
        {
            Uri url = PackageUrlDownload.ParseUrl(text, out string error);
            if (url == null)
            {
                MessageBoxes.Error(error);
                return;
            }
            urls.Add(url);
        }
        string directory;
        try
        {
            directory = PackageUrlDownloader.CreateDownloadDirectory();
        }
        catch (Exception exception)
        {
            MessageBoxes.Error("Cannot make a folder for the download in " + PackageUrlDownloader.DownloadsRoot + ": " + exception.Message);
            return;
        }

        CancellationTokenSource cancellation = new CancellationTokenSource();
        _Cancellation = cancellation;
        SetBusy(true);
        ILocalPackageFileInstaller installer = _Installer;
        Exception failure = null;
        string[] files = null;
        try
        {
            files = await Task.Run(() =>
            {
                List<string> downloaded = new List<string>();
                for (int i = 0; i < urls.Count; i++)
                {
                    int index = i;
                    string name = PackageUrlDownload.FileNameFromUri(urls[i]) ?? urls[i].Host;
                    downloaded.Add(PackageUrlDownloader.Download(urls[i], installer, directory, (long received, long total) => Report(index, urls.Count, name, received, total), cancellation.Token));
                }
                return downloaded.ToArray();
            });
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        _Cancellation = null;
        cancellation.Dispose();

        if (failure == null && _CloseWhenStopped)
        {
            // The last file arrived just as the user cancelled: cancelled is what they asked for.
            failure = new OperationCanceledException();
        }
        if (failure == null)
        {
            DownloadedFiles = files;
            DownloadDirectory = directory;
            DialogResult = true;
            return;
        }
        // Nothing is uploaded unless every link was downloaded: a package set with one of its dependencies missing would not install.
        DeleteDirectory(directory);
        SetBusy(false);
        if (_CloseWhenStopped)
        {
            DialogResult = false;
            return;
        }
        if (!(failure is OperationCanceledException))
        {
            MessageBoxes.Error(failure.Message);
        }
    }

    // Called on the download thread for every block: the window is updated at most ten times a second, and at the end of a file.
    private void Report(int index, int count, string name, long received, long total)
    {
        int now = Environment.TickCount;
        bool finished = total > 0 && received >= total;
        if (!finished && received != 0 && unchecked(now - _LastReport) < 100)
        {
            return;
        }
        _LastReport = now;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (!IsBusy)
            {
                return;
            }
            StatusText.Text = PackageUrlDownload.FormatProgress(index, count, name, received, total);
            double value = PackageUrlDownload.BarValue(index, count, received, total);
            Progress.IsIndeterminate = value < 0;
            if (value >= 0)
            {
                Progress.Value = value;
            }
        }));
    }

    private void SetBusy(bool busy)
    {
        UrlBox.IsReadOnly = busy;
        DownloadButton.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            StatusText.Text = "Connecting...";
            Progress.IsIndeterminate = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsBusy)
        {
            // Cancel (or Esc, or the close button) during a download stops it first; the window closes when the partial file is removed.
            e.Cancel = true;
            _CloseWhenStopped = true;
            StatusText.Text = "Cancelling...";
            _Cancellation.Cancel();
            return;
        }
        base.OnClosing(e);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Removed with the stale downloads next time (PackageUrlDownloader.CreateDownloadDirectory).
        }
    }
}
