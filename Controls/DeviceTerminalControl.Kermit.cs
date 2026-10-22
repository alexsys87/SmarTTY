using SmarTTY.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// Kermit over the COM/TCP connection (IO/KermitTransfer.cs holds the protocol). A transfer owns the line exactly like an
// XMODEM or ZMODEM one: everything received goes to the protocol (through the same queue) and typing is refused until it
// ends. Started from the menu only: the user starts the Kermit program on the device first.
public partial class DeviceTerminalControl
{
    private void InitializeKermitMenu()
    {
        sendFilesViaKermitToolStripMenuItem.Click -= sendFilesViaKermitToolStripMenuItem_Click;
        sendFilesViaKermitToolStripMenuItem.Click += sendFilesViaKermitToolStripMenuItem_Click;
        receiveFilesViaKermitToolStripMenuItem.Click -= receiveFilesViaKermitToolStripMenuItem_Click;
        receiveFilesViaKermitToolStripMenuItem.Click += receiveFilesViaKermitToolStripMenuItem_Click;
    }

    private void sendFilesViaKermitToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            MessageBox.Show("Please connect first before sending files via Kermit.", "Kermit Send", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "Select files to send via Kermit (start the Kermit receiver on the device first, e.g. \"kermit -r\")",
            Filter = "All files (*.*)|*.*",
            Multiselect = true
        };
        if (openFileDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        List<KermitTransfer.Source> sources = new List<KermitTransfer.Source>();
        foreach (string path in openFileDialog.FileNames)
        {
            string fileName = path;
            sources.Add(new KermitTransfer.Source(Path.GetFileName(fileName), new FileInfo(fileName).Length, () => File.OpenRead(fileName)));
        }
        StartLineTransfer("Kermit Send", "Kermit", "send", (sendData, progress, token) =>
            KermitTransfer.SendFiles(sources, sendData.Invoke, ReadXModemByte, token, progress.Invoke));
    }

    private void receiveFilesViaKermitToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            MessageBox.Show("Please connect first before receiving files via Kermit.", "Kermit Receive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using FolderBrowserDialog folderDialog = new FolderBrowserDialog
        {
            Description = "Select the folder for the files received via Kermit (then start the sender on the device, e.g. \"kermit -s file\")"
        };
        if (folderDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string folder = folderDialog.SelectedPath;
        Dictionary<string, string> created = new Dictionary<string, string>();
        StartLineTransfer("Kermit Receive", "Kermit", "receive", (sendData, progress, token) =>
            KermitTransfer.ReceiveFiles(
                name =>
                {
                    // An existing file is never overwritten: the new one gets " (1)", " (2)"... before the extension.
                    string path = Path.Combine(folder, name);
                    for (int i = 1; File.Exists(path); i++)
                    {
                        path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                    }
                    created[name] = path;
                    return File.Create(path);
                },
                name =>
                {
                    if (created.TryGetValue(name, out string path))
                    {
                        File.Delete(path);
                    }
                },
                sendData.Invoke, ReadXModemByte, token, progress.Invoke));
    }
}
