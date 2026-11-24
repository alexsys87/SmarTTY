using SmarTTY.Terminal.Input;
using SmarTTY.Controls;
using SmarTTY.GUI.TerminalView;
using SmarTTY.IO;
using System;
using System.IO;
using System.Linq;
using VisualTTY.DeviceTerminal;

using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace SmarTTY.GUI.DevicePanel;

// Files dropped on the terminal: a window asks which protocol to send them with, then the same code runs as for the
// toolbar commands (XMODEM, YMODEM, ZMODEM, Kermit, "Send file").
public partial class WpfDeviceTerminal
{

    // The terminals are WPF elements: the panel takes the drop of files for all of them (WpfTerminal accepts drops and leaves them to it).
    private void InitializeFileDrop()
    {
        AllowDrop = true;
        PreviewDragOver += FileDrop_DragOver;
        PreviewDragEnter += FileDrop_DragOver;
        PreviewDrop += FileDrop_Drop;
    }

    private void FileDrop_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool acceptFiles = e.Data != null && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
        e.Effects = acceptFiles ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDrop_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data == null || !(e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] dropped) || dropped.Length == 0)
        {
            return;
        }
        e.Handled = true;
        string[] droppedPaths = (string[])dropped.Clone();
        // After the drop has finished, so that Explorer is not kept waiting while the window is open.
        try
        {
            BeginInvoke((Action)(() => HandleDroppedFiles(droppedPaths)));
        }
        catch (InvalidOperationException)
        {
            // The terminal was closed while the drop was being delivered.
        }
    }

    private void HandleDroppedFiles(string[] dropped)
    {
        string[] files = dropped.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            UserDialogs.Show("Only files can be sent. Folders are not supported by the serial file transfer protocols.", "Send file", DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            UserDialogs.Show("Please connect first before sending a file.", "Send file", DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        Func<string[], DeviceFileDropProtocol?> prompt = FileDropProtocolPrompt;
        if (prompt == null)
        {
            return;
        }
        DeviceFileDropProtocol? choice = prompt(files);
        if (choice == null)
        {
            return;
        }
        // The window was modal, but the event loop kept running: a transfer may have started or the connection may be gone.
        if (IsLineBusy() || _connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            return;
        }
        SendDroppedFiles(files, choice.Value);
    }

    private void SendDroppedFiles(string[] files, DeviceFileDropProtocol protocol)
    {
        switch (protocol)
        {
            case DeviceFileDropProtocol.XModem:
            case DeviceFileDropProtocol.XModem1K:
                if (files.Length != 1)
                {
                    UserDialogs.Show("XMODEM sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "XMODEM Send", DialogButtons.Ok, DialogIcon.Information);
                    return;
                }
                StartXModemSend(files[0], useLargeBlocks: protocol == DeviceFileDropProtocol.XModem1K);
                break;
            case DeviceFileDropProtocol.YModem:
                StartYModemSend(files);
                break;
            case DeviceFileDropProtocol.ZModem:
                StartZModemSend(files);
                break;
            case DeviceFileDropProtocol.Kermit:
                StartKermitSend(files);
                break;
            case DeviceFileDropProtocol.GzipBase64:
                if (files.Length != 1)
                {
                    UserDialogs.Show("The gzip + base64 transfer sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "gzip + base64 Send", DialogButtons.Ok, DialogIcon.Information);
                    return;
                }
                StartGzipBase64Transfer(sending: true, droppedFile: files[0]);
                break;
            case DeviceFileDropProtocol.GzipCat:
                if (files.Length != 1)
                {
                    UserDialogs.Show("The gzip + cat transfer sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "gzip + cat Send", DialogButtons.Ok, DialogIcon.Information);
                    return;
                }
                StartGzipBase64Transfer(sending: true, droppedFile: files[0], raw: true);
                break;
            case DeviceFileDropProtocol.RawSend:
                if (files.Length != 1)
                {
                    UserDialogs.Show("A raw send writes one file to the line. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "Send file", DialogButtons.Ok, DialogIcon.Information);
                    return;
                }
                StartFileSend(files[0]);
                break;
        }
    }
}
