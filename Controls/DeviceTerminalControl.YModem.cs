using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// YMODEM: a batch of files with their names and sizes, over the same line-claiming transfer runner as XMODEM
// (StartLineTransfer). The protocol itself is SmarTTY.IO.YModemTransfer.
public partial class DeviceTerminalControl
{
    private void InitializeYModemMenu()
    {
        sendFilesViaYMODEMToolStripMenuItem.Click -= sendFilesViaYMODEMToolStripMenuItem_Click;
        sendFilesViaYMODEMToolStripMenuItem.Click += sendFilesViaYMODEMToolStripMenuItem_Click;
        receiveFilesViaYMODEMToolStripMenuItem.Click -= receiveFilesViaYMODEMToolStripMenuItem_Click;
        receiveFilesViaYMODEMToolStripMenuItem.Click += receiveFilesViaYMODEMToolStripMenuItem_Click;
        receiveFilesViaYMODEMGToolStripMenuItem.Click -= receiveFilesViaYMODEMGToolStripMenuItem_Click;
        receiveFilesViaYMODEMGToolStripMenuItem.Click += receiveFilesViaYMODEMGToolStripMenuItem_Click;
    }

    private void sendFilesViaYMODEMToolStripMenuItem_Click(object sender, EventArgs e)
    {
        SendFilesViaYModem();
    }

    private void receiveFilesViaYMODEMToolStripMenuItem_Click(object sender, EventArgs e)
    {
        ReceiveFilesViaYModem(streaming: false);
    }

    private void receiveFilesViaYMODEMGToolStripMenuItem_Click(object sender, EventArgs e)
    {
        ReceiveFilesViaYModem(streaming: true);
    }

    private void SendFilesViaYModem()
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before sending files via YMODEM.", "YMODEM Send", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string[] fileNames = ThemedFileDialog.OpenFiles(this, "Select file(s) to send via YMODEM", "All files (*.*)|*.*");
        if (fileNames == null)
        {
            return;
        }

        // The files are opened later, on the worker thread: the names are already taken above.

        StartLineTransfer("YMODEM Send", "YMODEM", "send", (sendData, progress, token) =>
        {
            List<FileStream> opened = new List<FileStream>();
            try
            {
                List<SmarTTY.IO.YModemTransfer.FileToSend> files = new List<SmarTTY.IO.YModemTransfer.FileToSend>();
                foreach (string path in fileNames)
                {
                    FileStream stream = File.OpenRead(path);
                    opened.Add(stream);
                    files.Add(new SmarTTY.IO.YModemTransfer.FileToSend(Path.GetFileName(path), stream, File.GetLastWriteTimeUtc(path)));
                }
                return SmarTTY.IO.YModemTransfer.SendFiles(files, sendData, ReadXModemByte, token, progress);
            }
            finally
            {
                foreach (FileStream stream in opened)
                {
                    stream.Dispose();
                }
            }
        });
    }

    /// <summary>
    /// Receives files. The standard receiver asks for CRC-16 and falls back to the checksum variant by itself; YMODEM-G
    /// (<paramref name="streaming"/>) has to be asked for because the sender cannot tell the difference.
    /// </summary>
    private void ReceiveFilesViaYModem(bool streaming)
    {
        string protocolName = streaming ? "YMODEM-G" : "YMODEM";
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before receiving files via " + protocolName + ".", protocolName + " Receive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string folder = ThemedFolderBrowser.Browse(this,
            "Select the folder to save the received file(s) to. The names come from the sender; an existing file is never overwritten.");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        // Touched only by the transfer worker while it runs, and read after it has ended.
        List<string> saved = new List<string>();
        Dictionary<int, string> pathByIndex = new Dictionary<int, string>();
        string[] incomplete = new string[1]; // the file being written right now

        StartLineTransfer(protocolName + " Receive", protocolName, "receive", (sendData, progress, token) =>
        {
            bool success = false;
            try
            {
                success = SmarTTY.IO.YModemTransfer.ReceiveFiles(
                    header =>
                    {
                        string path = UniqueFilePathIn(folder, header.Name);
                        FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        pathByIndex[header.Index] = path;
                        incomplete[0] = path;
                        return stream;
                    },
                    sendData, ReadXModemByte, token, progress,
                    header =>
                    {
                        string path = pathByIndex[header.Index];
                        incomplete[0] = null;
                        saved.Add(path);
                        if (header.ModifiedUtc.HasValue)
                        {
                            try
                            {
                                File.SetLastWriteTimeUtc(path, header.ModifiedUtc.Value);
                            }
                            catch (Exception)
                            {
                                // The time is a courtesy: the file itself is fine.
                            }
                        }
                    },
                    streaming ? SmarTTY.IO.YModemTransfer.YModemReceiveMode.Streaming : SmarTTY.IO.YModemTransfer.YModemReceiveMode.Standard);
                return success;
            }
            finally
            {
                // A file that stopped half way must not sit in the folder under its real name: it would pass for the whole file.
                if (!success && incomplete[0] != null)
                {
                    try
                    {
                        File.Delete(incomplete[0]);
                    }
                    catch (Exception)
                    {
                        // Nothing more can be done about it here.
                    }
                }
            }
        },
        successNote: () => saved.Count == 0
            ? null
            : (saved.Count == 1 ? "Saved as:" : saved.Count + " files saved to " + folder + ":")
              + "\r\n" + string.Join("\r\n", saved.Select(path => saved.Count == 1 ? path : Path.GetFileName(path))));
    }

    /// <summary>
    /// A path for <paramref name="fileName"/> inside <paramref name="folder"/> that does not exist yet: the plain name,
    /// or "name (1).ext", "name (2).ext"... A file sent by a device must not replace one that is already there.
    /// </summary>
    private static string UniqueFilePathIn(string folder, string fileName)
    {
        string folderFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int attempt = 0; attempt < 10000; attempt++)
        {
            string candidate = attempt == 0 ? fileName : stem + " (" + attempt + ")" + extension;
            string path = Path.GetFullPath(Path.Combine(folder, candidate));

            // The name was reduced to a plain file name by the protocol layer; this is the second lock on the same door.
            string parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(parent, folderFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The file name \"" + fileName + "\" does not stay inside the chosen folder.");
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
        }
        throw new IOException("No free name for \"" + fileName + "\" in " + folder + ".");
    }
}
