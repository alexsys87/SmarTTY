using Microsoft.Win32;
using SmarTTY.IO;
using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Asks what to send to the device (or fetch from it) with the gzip + base64 transfer: the file, the path on the device and
/// the options of <see cref="GzipBase64Transfer.Options"/>. What was chosen last time is offered again.
/// </summary>
public partial class GzipBase64TransferWindow : Window
{
    // Kept for the next time the window opens (not saved between runs).
    private static string _lastRemoteSendFolder = "/tmp/";
    private static string _lastRemoteReceivePath = string.Empty;
    private static string _lastLocalFolder = string.Empty;
    private static int _lastEncoding;
    private static int _lastChecksum;
    private static string _lastAttempts = "2";
    private static string _lastRate = "6000";

    private readonly bool _sending;

    internal GzipBase64Transfer.Request Request { get; private set; }

    private GzipBase64TransferWindow(bool sending)
    {
        InitializeComponent();
        _sending = sending;
        EncodingBox.SelectedIndex = _lastEncoding;
        ChecksumBox.SelectedIndex = _lastChecksum;
        AttemptsBox.Text = _lastAttempts;
        RateBox.Text = _lastRate;
        if (sending)
        {
            Title = "Send file via gzip + base64";
            IntroText.Text = "Sends a file to the device through its shell, with no file transfer program on the device.";
            LocalLabel.Text = "File to send:";
            RemoteLabel.Text = "Save on the device as:";
            RemoteBox.Text = _lastRemoteSendFolder;
            RemoteHint.Text = "A file name, or a folder (the file keeps its name), e.g. /tmp/ or /root/firmware.bin.";
            OverwriteBox.Content = "Replace the file on the device if it exists";
            StartButton.Content = "_Send";
        }
        else
        {
            Title = "Receive file via gzip + base64";
            IntroText.Text = "Fetches a file from the device through its shell, with no file transfer program on the device.";
            LocalLabel.Text = "Save as:";
            RemoteLabel.Text = "File on the device:";
            RemoteBox.Text = _lastRemoteReceivePath;
            RemoteHint.Text = "The full path of the file on the device, e.g. /etc/config/network.";
            OverwriteBox.Content = "Replace the file on this computer if it exists";
            StartButton.Content = "_Receive";
        }
    }

    /// <summary>Returns the request, or null when the user cancelled.</summary>
    internal static GzipBase64Transfer.Request ShowSend(Window owner)
    {
        return Show(owner, sending: true);
    }

    /// <summary>Returns the request, or null when the user cancelled.</summary>
    internal static GzipBase64Transfer.Request ShowReceive(Window owner)
    {
        return Show(owner, sending: false);
    }

    private static GzipBase64Transfer.Request Show(Window owner, bool sending)
    {
        GzipBase64TransferWindow window = new GzipBase64TransferWindow(sending);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window.Request : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Select the file to send",
                CheckFileExists = true,
                InitialDirectory = ExistingFolder(_lastLocalFolder)
            };
            if (dialog.ShowDialog(this) == true)
            {
                LocalBox.Text = dialog.FileName;
                if (RemoteBox.Text.Trim().Length == 0)
                {
                    RemoteBox.Text = _lastRemoteSendFolder;
                }
            }
        }
        else
        {
            string suggested = RemoteBox.Text.Trim();
            int slash = suggested.LastIndexOf('/');
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = "Save the received file as",
                OverwritePrompt = true,
                FileName = slash >= 0 ? suggested.Substring(slash + 1) : suggested,
                InitialDirectory = ExistingFolder(_lastLocalFolder)
            };
            if (dialog.ShowDialog(this) == true)
            {
                LocalBox.Text = dialog.FileName;
                // The dialog has already asked about replacing an existing file.
                OverwriteBox.IsChecked = true;
            }
        }
    }

    private static string ExistingFolder(string folder)
    {
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : string.Empty;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        string local = LocalBox.Text.Trim().Trim('"');
        string remote = RemoteBox.Text.Trim();

        if (local.Length == 0)
        {
            MessageBoxes.Warning(_sending ? "Choose the file to send." : "Choose where to save the received file.");
            LocalBox.Focus();
            return;
        }
        if (_sending)
        {
            if (!File.Exists(local))
            {
                MessageBoxes.Warning("The file does not exist:\n" + local);
                LocalBox.Focus();
                return;
            }
        }
        else
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(local));
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBoxes.Warning("The folder does not exist:\n" + folder);
                LocalBox.Focus();
                return;
            }
            if (File.Exists(local) && OverwriteBox.IsChecked != true)
            {
                if (!MessageBoxes.Ask("The file already exists. Replace it?\n" + local))
                {
                    return;
                }
                OverwriteBox.IsChecked = true;
            }
        }
        if (remote.Length == 0)
        {
            MessageBoxes.Warning(_sending ? "Enter where to put the file on the device." : "Enter the path of the file on the device.");
            RemoteBox.Focus();
            return;
        }
        if (!int.TryParse(AttemptsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts) || attempts < 1 || attempts > 9)
        {
            MessageBoxes.Warning("Attempts must be a number from 1 to 9.");
            AttemptsBox.Focus();
            return;
        }
        if (!int.TryParse(RateBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate) || rate < 500 || rate > 1000000)
        {
            MessageBoxes.Warning("The speed must be a number from 500 to 1000000 bytes per second.");
            RateBox.Focus();
            return;
        }

        GzipBase64Transfer.Options options = new GzipBase64Transfer.Options
        {
            Encoding = (GzipBase64Transfer.EncodingKind)EncodingBox.SelectedIndex,
            Checksum = (GzipBase64Transfer.ChecksumKind)ChecksumBox.SelectedIndex,
            Attempts = attempts,
            Overwrite = OverwriteBox.IsChecked == true,
            NoEchoBytesPerSecond = rate
        };
        Request = new GzipBase64Transfer.Request { LocalPath = local, RemotePath = remote, Options = options };

        _lastEncoding = EncodingBox.SelectedIndex;
        _lastChecksum = ChecksumBox.SelectedIndex;
        _lastAttempts = attempts.ToString(CultureInfo.InvariantCulture);
        _lastRate = rate.ToString(CultureInfo.InvariantCulture);
        _lastLocalFolder = Path.GetDirectoryName(Path.GetFullPath(local)) ?? string.Empty;
        if (_sending)
        {
            int slash = remote.LastIndexOf('/');
            _lastRemoteSendFolder = remote.EndsWith("/", StringComparison.Ordinal) ? remote : slash >= 0 ? remote.Substring(0, slash + 1) : _lastRemoteSendFolder;
        }
        else
        {
            _lastRemoteReceivePath = remote;
        }
        DialogResult = true;
    }
}
