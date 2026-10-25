using SmarTTY.IO;
using System;
using System.IO;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// File transfer through the device's shell: the file is packed with gzip and typed into the shell as base64 (or uuencode)
// text, the device decodes and unpacks it and the checksums are compared (IO/GzipBase64Transfer.cs holds the protocol). It
// needs nothing on the device but busybox tools, so it works on any Linux (OpenWrt included) that has a shell on this COM/TCP
// connection. A transfer owns the line exactly like an XMODEM or Kermit one (StartLineTransfer is shared, with the WPF
// progress window of the other protocols). The window that asks what to transfer is WPF (GUI/GzipBase64TransferWindow.xaml);
// this file is compiled by the Mono test harness, which cannot reference GUI/, so App injects it through
// GzipBase64RequestPrompt.
public partial class DeviceTerminalControl
{
    /// <summary>
    /// Asks what to transfer: the argument is true to send a file, false to receive one. Returns null when the user
    /// cancelled. Injected by <c>App</c> (the window is WPF).
    /// </summary>
    public static Func<bool, GzipBase64Transfer.Request> GzipBase64RequestPrompt;

    private void InitializeGzipBase64Menu()
    {
        ToolStripMenuItem send = new ToolStripMenuItem("Send file via gzip + base64 (shell)...")
        {
            ToolTipText = "Sends a file through the shell of a Linux device (OpenWrt too): packed with gzip, typed as base64 text, unpacked there and checked with a checksum. Nothing is needed on the device but busybox tools."
        };
        ToolStripMenuItem receive = new ToolStripMenuItem("Receive file via gzip + base64 (shell)...")
        {
            ToolTipText = "Fetches a file through the shell of a Linux device (OpenWrt too): packed there with gzip, sent as base64 text, unpacked here and checked with a checksum."
        };
        Icons.Apply(send, "upload");
        Icons.Apply(receive, "download");
        send.Click += (sender, e) => StartGzipBase64Transfer(sending: true);
        receive.Click += (sender, e) => StartGzipBase64Transfer(sending: false);
        toolStripButton3.DropDownItems.Add(send);
        toolStripButton3.DropDownItems.Add(receive);
    }

    /// <summary>Asks what to transfer and runs the transfer over the shared line runner. Must be called on the UI thread.</summary>
    private void StartGzipBase64Transfer(bool sending)
    {
        string caption = sending ? "gzip + base64 Send" : "gzip + base64 Receive";
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before using gzip + base64 transfer.", caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Func<bool, GzipBase64Transfer.Request> prompt = GzipBase64RequestPrompt;
        if (prompt == null)
        {
            ThemedMessageBox.Show("The gzip + base64 transfer window is not available.", caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        GzipBase64Transfer.Request request = prompt(sending);
        if (request == null)
        {
            return;
        }

        if (sending)
        {
            StartLineTransfer("Send via gzip + base64 - " + Path.GetFileName(request.LocalPath), "gzip + base64", "send",
                (sendData, progress, token) => GzipBase64Transfer.SendFile(request.LocalPath, request.RemotePath, request.Options, sendData, ReadXModemByte, token, progress),
                () => "The file arrived on the device and its checksum matches the original:\r\n" + request.RemotePath,
                SendEnterToDevice);
        }
        else
        {
            StartLineTransfer("Receive via gzip + base64 - " + Path.GetFileName(request.RemotePath), "gzip + base64", "receive",
                (sendData, progress, token) => GzipBase64Transfer.ReceiveFile(request.RemotePath, request.LocalPath, request.Options, sendData, ReadXModemByte, token, progress),
                () => "The file was saved and its checksum matches the original on the device:\r\n" + request.LocalPath,
                SendEnterToDevice);
        }
    }

    /// <summary>
    /// The shell prompt that appeared during the transfer was taken by it; Enter makes the device print a fresh one
    /// in the terminal.
    /// </summary>
    private void SendEnterToDevice()
    {
        SendToDevice(new byte[] { (byte)'\r' }, 1);
    }
}
