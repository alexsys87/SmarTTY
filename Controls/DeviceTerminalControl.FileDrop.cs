using SmarTTY.IO;
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

/// <summary>How a file dropped on a COM-port / TCP terminal is sent to the device.</summary>
public enum DeviceFileDropProtocol
{
    XModem,
    XModem1K,
    YModem,
    ZModem,
    Kermit,
    /// <summary>Through the shell of a Linux device: packed with gzip, typed as base64, unpacked there (one file).</summary>
    GzipBase64,
    /// <summary>The bytes of the file are written to the line as they are, with no protocol.</summary>
    RawSend,
    /// <summary>Through the shell of a Linux device: packed with gzip and sent as it is, with no text form, read there in raw mode (one file).</summary>
    GzipCat,
}

// Files dropped on the terminal: a window asks which protocol to send them with, then the same code runs as for the
// toolbar commands (XMODEM, YMODEM, ZMODEM, Kermit, "Send file").
public partial class DeviceTerminalControl
{
    /// <summary>
    /// Asks how the dropped files are to be sent (the full paths of the files). Returns the chosen protocol, or null when
    /// the user cancelled. Injected by <c>App</c> (the window is WPF, which the test harness does not compile), like the
    /// other prompts of this control. Null when no window is available: a drop is then ignored.
    /// </summary>
    public static Func<string[], DeviceFileDropProtocol?> FileDropProtocolPrompt;

    private void InitializeFileDrop()
    {
        HookFileDrop(this);
    }

    /// <summary>
    /// Asks for the gzip + base64 transfer of the dropped file (its full path): the window also needs the path on the device.
    /// Returns the request, or null when the user cancelled. Injected by <c>App</c>, like <see cref="FileDropProtocolPrompt"/>.
    /// </summary>
    public static Func<string, GzipBase64Transfer.Request> GzipBase64DropPrompt;

    /// <summary>The same for the raw form, gzip + cat (the request then has <see cref="GzipBase64Transfer.EncodingKind.Raw"/>).</summary>
    public static Func<string, GzipBase64Transfer.Request> GzipCatDropPrompt;

    /// <summary>
    /// Every child control has to accept the drop itself: a WinForms control that does not allow drops hides the
    /// ones below it. Text boxes and combo boxes are left alone, so that they keep their own text drop behavior.
    /// </summary>
    private void HookFileDrop(Control control)
    {
        if (control is TextBoxBase || control is ComboBox)
        {
            return;
        }
        control.AllowDrop = true;
        // Unsubscribe first: a control that is added again must not get the handlers twice.
        control.DragEnter -= FileDrop_DragOver;
        control.DragOver -= FileDrop_DragOver;
        control.DragDrop -= FileDrop_DragDrop;
        control.ControlAdded -= FileDrop_ControlAdded;
        control.DragEnter += FileDrop_DragOver;
        control.DragOver += FileDrop_DragOver;
        control.DragDrop += FileDrop_DragDrop;
        control.ControlAdded += FileDrop_ControlAdded;
        foreach (Control child in control.Controls)
        {
            HookFileDrop(child);
        }
    }

    private void FileDrop_ControlAdded(object sender, ControlEventArgs e)
    {
        HookFileDrop(e.Control);
    }

    private void FileDrop_DragOver(object sender, DragEventArgs e)
    {
        bool acceptFiles = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = acceptFiles ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void FileDrop_DragDrop(object sender, DragEventArgs e)
    {
        if (e.Data == null || !(e.Data.GetData(DataFormats.FileDrop) is string[] dropped) || dropped.Length == 0)
        {
            return;
        }
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
            ThemedMessageBox.Show("Only files can be sent. Folders are not supported by the serial file transfer protocols.", "Send file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before sending a file.", "Send file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    ThemedMessageBox.Show("XMODEM sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "XMODEM Send", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    ThemedMessageBox.Show("The gzip + base64 transfer sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "gzip + base64 Send", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StartGzipBase64Transfer(sending: true, droppedFile: files[0]);
                break;
            case DeviceFileDropProtocol.GzipCat:
                if (files.Length != 1)
                {
                    ThemedMessageBox.Show("The gzip + cat transfer sends one file at a time. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "gzip + cat Send", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StartGzipBase64Transfer(sending: true, droppedFile: files[0], raw: true);
                break;
            case DeviceFileDropProtocol.RawSend:
                if (files.Length != 1)
                {
                    ThemedMessageBox.Show("A raw send writes one file to the line. Drop a single file, or choose YMODEM, ZMODEM or Kermit.", "Send file", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                StartFileSend(files[0]);
                break;
        }
    }
}
