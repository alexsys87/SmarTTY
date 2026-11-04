using SmarTTY.Controls;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmarTTY.GUI;

/// <summary>
/// Opens when files are dropped on a COM-port / TCP terminal: asks which file transfer protocol (XMODEM, YMODEM, ZMODEM,
/// Kermit or raw data) to send them with. The protocol picked last time is offered again (until the program ends).
/// </summary>
public partial class FileTransferProtocolWindow : Window
{
    private static DeviceFileDropProtocol _lastProtocol = DeviceFileDropProtocol.YModem;

    private readonly bool _singleFile;

    internal DeviceFileDropProtocol Protocol { get; private set; }

    private FileTransferProtocolWindow(string[] files)
    {
        InitializeComponent();
        _singleFile = files.Length == 1;
        if (_singleFile)
        {
            HeaderText.Text = "Send 1 file to the device";
            long length = SafeLength(files[0]);
            FilesList.Items.Add(Path.GetFileName(files[0]) + (length >= 0 ? "  (" + FormatSize(length) + ")" : string.Empty));
            FilesList.ToolTip = files[0];
        }
        else
        {
            HeaderText.Text = "Send " + files.Length + " files to the device";
            foreach (string file in files)
            {
                long length = SafeLength(file);
                FilesList.Items.Add(Path.GetFileName(file) + (length >= 0 ? "  (" + FormatSize(length) + ")" : string.Empty));
            }
        }
        // XMODEM, gzip + base64 and a raw send carry one file only.
        XModemButton.IsEnabled = _singleFile;
        XModem1KButton.IsEnabled = _singleFile;
        RawButton.IsEnabled = _singleFile;
        GzipButton.IsEnabled = _singleFile;
        DeviceFileDropProtocol start = _lastProtocol;
        if (!_singleFile && (start == DeviceFileDropProtocol.XModem || start == DeviceFileDropProtocol.XModem1K || start == DeviceFileDropProtocol.RawSend || start == DeviceFileDropProtocol.GzipBase64))
        {
            start = DeviceFileDropProtocol.YModem;
        }
        ButtonFor(start).IsChecked = true;
        UpdateHint();
    }

    /// <summary>Returns the protocol chosen for the files, or null when the user cancelled.</summary>
    internal static DeviceFileDropProtocol? Ask(Window owner, string[] files)
    {
        FileTransferProtocolWindow window = new FileTransferProtocolWindow(files);
        WindowOwnership.Attach(window, owner ?? WindowOwnership.ActiveWindow());
        if (window.ShowDialog() != true)
        {
            return null;
        }
        _lastProtocol = window.Protocol;
        return window.Protocol;
    }

    private RadioButton ButtonFor(DeviceFileDropProtocol protocol)
    {
        switch (protocol)
        {
            case DeviceFileDropProtocol.XModem: return XModemButton;
            case DeviceFileDropProtocol.XModem1K: return XModem1KButton;
            case DeviceFileDropProtocol.ZModem: return ZModemButton;
            case DeviceFileDropProtocol.Kermit: return KermitButton;
            case DeviceFileDropProtocol.GzipBase64: return GzipButton;
            case DeviceFileDropProtocol.RawSend: return RawButton;
            default: return YModemButton;
        }
    }

    private DeviceFileDropProtocol Selected()
    {
        if (XModemButton.IsChecked == true) return DeviceFileDropProtocol.XModem;
        if (XModem1KButton.IsChecked == true) return DeviceFileDropProtocol.XModem1K;
        if (ZModemButton.IsChecked == true) return DeviceFileDropProtocol.ZModem;
        if (KermitButton.IsChecked == true) return DeviceFileDropProtocol.Kermit;
        if (GzipButton.IsChecked == true) return DeviceFileDropProtocol.GzipBase64;
        if (RawButton.IsChecked == true) return DeviceFileDropProtocol.RawSend;
        return DeviceFileDropProtocol.YModem;
    }

    private void Protocol_Checked(object sender, RoutedEventArgs e)
    {
        // Checked is also raised while the window is built, before HintText exists.
        if (HintText != null)
        {
            UpdateHint();
        }
    }

    private void UpdateHint()
    {
        switch (Selected())
        {
            case DeviceFileDropProtocol.XModem:
            case DeviceFileDropProtocol.XModem1K:
                HintText.Text = "The receiver must already be running on the device (for example \"rx file\" in a shell or the loadx command of the boot loader): while the file is being sent, the terminal belongs to the transfer. The sender waits up to 90 seconds for it.";
                break;
            case DeviceFileDropProtocol.YModem:
                HintText.Text = "The receiver must already be running on the device (for example \"rb\" or the loady command of the boot loader): while the files are being sent, the terminal belongs to the transfer. The sender waits up to 90 seconds for it.";
                break;
            case DeviceFileDropProtocol.ZModem:
                HintText.Text = "At an idle shell prompt of the device rz is started automatically. In any other program (a boot loader, for example) start the ZMODEM receiver on the device before you drop the file.";
                break;
            case DeviceFileDropProtocol.Kermit:
                HintText.Text = "The receiver must already be running on the device (for example \"kermit -r\" or the loadb command of the boot loader): while the files are being sent, the terminal belongs to the transfer. The sender offers the file for up to 60 seconds.";
                break;
            case DeviceFileDropProtocol.GzipBase64:
                HintText.Text = "Needs an idle shell prompt of a Linux device (OpenWrt too) with gzip, base64 and sha256sum; busybox is enough. The next window asks where to put the file on the device.";
                break;
            default:
                HintText.Text = "The file is sent as it is. Make the device ready to take the data before you drop the file (for example \"cat > file\" in a shell). Nothing checks that the data arrived.";
                break;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        Protocol = Selected();
        DialogResult = true;
    }

    private void Protocol_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is RadioButton button && button.IsEnabled)
        {
            Send_Click(sender, e);
        }
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }
        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
    }
}
