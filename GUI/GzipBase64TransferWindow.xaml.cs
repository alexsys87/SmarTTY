using SmarTTY.Controls;
using SmarTTY.IO;
using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace SmarTTY.GUI;

/// <summary>
/// Asks what to send to the device (or fetch from it) with the gzip + base64 transfer: the file, the path on the device and
/// the options of <see cref="GzipBase64Transfer.Options"/>. What was chosen last time is offered again. The same window asks for
/// the raw form, gzip + cat (<see cref="GzipBase64Transfer.EncodingKind.Raw"/>): there is no text form to choose and no line
/// length, and the speed limit is the speed of the whole stream.
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
    private static string _lastBlock = "1024";
    private static string _lastRawRate = "6000";

    private readonly bool _sending;
    private readonly bool _raw;

    internal GzipBase64Transfer.Request Request { get; private set; }

    private GzipBase64TransferWindow(bool sending, string localPath, bool raw)
    {
        InitializeComponent();
        _sending = sending;
        _raw = raw;
        EncodingBox.SelectedIndex = _lastEncoding;
        ChecksumBox.SelectedIndex = _lastChecksum;
        AttemptsBox.Text = _lastAttempts;
        RateBox.Text = raw ? _lastRawRate : _lastRate;
        BlockBox.Text = _lastBlock;
        if (raw)
        {
            // gzip + cat: the archive goes as it is, so there is no text form and no line to check against an echo.
            EncodingLabel.Visibility = Visibility.Collapsed;
            EncodingBox.Visibility = Visibility.Collapsed;
            BlockLabel.Visibility = Visibility.Collapsed;
            BlockPanel.Visibility = Visibility.Collapsed;
            RateLabel.Text = "Speed:";
            RateBox.ToolTip = "Bytes per second that the archive is sent at. There is no echo in raw mode to tell that the device has kept up, so keep it under what the device and its line can take: 11520 is the top of a 115200 baud line, 6000 is safe for most routers.";
            NoteText.Text = sending
                ? "Start this at an idle shell prompt of the device (not inside another program). The device needs gunzip, stty and cat (head -c or dd to count the bytes; without them the end of the data is found by 8 seconds of silence on the line) and sha256sum (or md5sum); OpenWrt has them in busybox. Nothing is installed on the device. The file is packed with gzip and sent as it is, without base64, unpacked there, then the checksums are compared. Nothing can be checked on the way, so a check that fails repeats the whole transfer. The file gets its name only when the checksums match."
                : "Start this at an idle shell prompt of the device (not inside another program). The device needs gzip, stty, wc and cat, and sha256sum (or md5sum); OpenWrt has them in busybox. Nothing is installed on the device. The file is packed with gzip there and sent as it is, without base64, then unpacked here and the checksums are compared. Nothing can be checked on the way, so a check that fails repeats the whole transfer.";
        }
        if (sending)
        {
            Title = raw ? "Send file via gzip + cat" : "Send file via gzip + base64";
            IntroText.Text = "Sends a file to the device through its shell, with no file transfer program on the device.";
            LocalLabel.Text = "File to send:";
            RemoteLabel.Text = "Save on the device as:";
            RemoteBox.Text = _lastRemoteSendFolder;
            RemoteHint.Text = "A file name, or a folder (the file keeps its name), e.g. /tmp/ or /root/firmware.bin.";
            OverwriteBox.Content = "Replace the file on the device if it exists";
            StartButton.Content = "_Send";
            if (!string.IsNullOrEmpty(localPath))
            {
                LocalBox.Text = localPath;
            }
        }
        else
        {
            Title = raw ? "Receive file via gzip + cat" : "Receive file via gzip + base64";
            IntroText.Text = "Fetches a file from the device through its shell, with no file transfer program on the device.";
            LocalLabel.Text = "Save as:";
            RemoteLabel.Text = "File on the device:";
            RemoteBox.Text = _lastRemoteReceivePath;
            RemoteHint.Text = "The full path of the file on the device, e.g. /etc/config/network.";
            OverwriteBox.Content = "Replace the file on this computer if it exists";
            StartButton.Content = "_Receive";
        }
    }

    /// <summary>Returns the request, or null when the user cancelled. With <paramref name="raw"/> it is a request for gzip + cat.</summary>
    internal static GzipBase64Transfer.Request ShowSend(Window owner, string localPath = null, bool raw = false)
    {
        return Show(owner, sending: true, localPath, raw);
    }

    /// <summary>Returns the request, or null when the user cancelled. With <paramref name="raw"/> it is a request for gzip + cat.</summary>
    internal static GzipBase64Transfer.Request ShowReceive(Window owner, bool raw = false)
    {
        return Show(owner, sending: false, localPath: null, raw);
    }

    private static GzipBase64Transfer.Request Show(Window owner, bool sending, string localPath, bool raw)
    {
        GzipBase64TransferWindow window = new GzipBase64TransferWindow(sending, localPath, raw);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        return window.ShowDialog() == true ? window.Request : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            string chosen = LocalFileDialogWindow.OpenFile(this, "Select the file to send", "All files (*.*)|*.*", initialDirectory: ExistingFolder(_lastLocalFolder));
            if (chosen != null)
            {
                LocalBox.Text = chosen;
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
            string chosen = LocalFileDialogWindow.SaveFile(this, "Save the received file as", "All files (*.*)|*.*",
                fileName: slash >= 0 ? suggested.Substring(slash + 1) : suggested, initialDirectory: ExistingFolder(_lastLocalFolder));
            if (chosen != null)
            {
                LocalBox.Text = chosen;
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

        if (!int.TryParse(BlockBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int block) || block < 64 || block > 2048)
        {
            MessageBoxes.Warning("The block size must be a number from 64 to 2048 characters.");
            BlockBox.Focus();
            return;
        }

        GzipBase64Transfer.Options options = new GzipBase64Transfer.Options
        {
            Encoding = _raw ? GzipBase64Transfer.EncodingKind.Raw : (GzipBase64Transfer.EncodingKind)EncodingBox.SelectedIndex,
            Checksum = (GzipBase64Transfer.ChecksumKind)ChecksumBox.SelectedIndex,
            Attempts = attempts,
            Overwrite = OverwriteBox.IsChecked == true,
            NoEchoBytesPerSecond = rate,
            WindowBytes = block
        };
        Request = new GzipBase64Transfer.Request { LocalPath = local, RemotePath = remote, Options = options };

        _lastChecksum = ChecksumBox.SelectedIndex;
        _lastAttempts = attempts.ToString(CultureInfo.InvariantCulture);
        if (_raw)
        {
            _lastRawRate = rate.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            _lastEncoding = EncodingBox.SelectedIndex;
            _lastRate = rate.ToString(CultureInfo.InvariantCulture);
            _lastBlock = block.ToString(CultureInfo.InvariantCulture);
        }
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
