using SmarTTY.Forms;
using SmarTTY.IO;
using SmarTTY.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using VisualTTY.DeviceTerminal;

namespace SmarTTY.Controls;

// ZMODEM over the COM/TCP connection (IO/ZModemTransfer.cs holds the protocol). A transfer owns the line exactly like
// an XMODEM one: everything received goes to the protocol (through the same queue) and typing is refused until it ends.
// A transfer is started from the menu, or automatically when the device begins to send (sz), which is recognized by
// the header every ZMODEM sender opens with; the user is asked where to put the files before anything is written.
public partial class DeviceTerminalControl
{
    // "**" ZDLE 'B' '0' '0': the hex ZRQINIT header that opens a send by sz/lrzsz and many embedded implementations.
    private static readonly byte[] ZModemRequestPattern = { 0x2A, 0x2A, 0x18, 0x42, 0x30, 0x30 };

    private volatile bool _autoStartZModem = true;

    // The end of the previous chunk of received data: the request may arrive split between two reads.
    private volatile byte[] _zmodemTail = new byte[0];

    // A refused or just finished transfer must not be started again by the sender's repeated request.
    private int _zmodemIgnoreUntil;

    private volatile bool _zmodemIgnoring;

    // The folder chosen last time, offered first next time (shared by all terminals of the program).
    private static string _zmodemFolder;

    /// <summary>
    /// Connects the ZMODEM menu items to their handlers. Done here and not in the designer file, which Visual Studio rewrites;
    /// "-=" first so the handler runs once even if the designer also connects it.
    /// </summary>
    private void InitializeZModemMenu()
    {
        sendFilesViaZModemToolStripMenuItem.Click -= sendFilesViaZModemToolStripMenuItem_Click;
        sendFilesViaZModemToolStripMenuItem.Click += sendFilesViaZModemToolStripMenuItem_Click;
        receiveFilesViaZModemToolStripMenuItem.Click -= receiveFilesViaZModemToolStripMenuItem_Click;
        receiveFilesViaZModemToolStripMenuItem.Click += receiveFilesViaZModemToolStripMenuItem_Click;
        autoReceiveZModemToolStripMenuItem.CheckedChanged -= autoReceiveZModemToolStripMenuItem_CheckedChanged;
        autoReceiveZModemToolStripMenuItem.CheckedChanged += autoReceiveZModemToolStripMenuItem_CheckedChanged;
        _autoStartZModem = autoReceiveZModemToolStripMenuItem.Checked;
    }

    private void autoReceiveZModemToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
    {
        _autoStartZModem = autoReceiveZModemToolStripMenuItem.Checked;
        _zmodemTail = new byte[0];
    }

    /// <summary>
    /// The index in <paramref name="buffer"/> where a ZMODEM request starts, or -1. A request that began at the end of the
    /// previous chunk is reported as 0 (the rest of it is at the start of this chunk).
    /// </summary>
    private int FindZModemRequest(byte[] buffer, int count)
    {
        if (_zmodemIgnoring && unchecked(Environment.TickCount - _zmodemIgnoreUntil) < 0)
        {
            _zmodemTail = new byte[0];
            return -1;
        }

        byte[] tail = _zmodemTail;
        int patternLength = ZModemRequestPattern.Length;
        int total = tail.Length + count;
        int found = -1;
        for (int i = 0; i + patternLength <= total && found < 0; i++)
        {
            int j = 0;
            while (j < patternLength)
            {
                int at = i + j;
                byte b = at < tail.Length ? tail[at] : buffer[at - tail.Length];
                if (b != ZModemRequestPattern[j])
                {
                    break;
                }
                j++;
            }
            if (j == patternLength)
            {
                found = i;
            }
        }

        int keep = Math.Min(patternLength - 1, total);
        byte[] newTail = new byte[keep];
        for (int k = 0; k < keep; k++)
        {
            int at = total - keep + k;
            newTail[k] = at < tail.Length ? tail[at] : buffer[at - tail.Length];
        }
        _zmodemTail = newTail;

        if (found < 0)
        {
            return -1;
        }
        return found < tail.Length ? 0 : found - tail.Length;
    }

    /// <summary>
    /// The device started a ZMODEM send: claim the line right away (the bytes that follow belong to the protocol) and ask
    /// the user where to save the files. Runs on the UI thread. False when it cannot be started.
    /// </summary>
    private bool BeginAutoZModemReceive(byte[] buffer, int start, int count)
    {
        if (!IsHandleCreated || IsDisposed || _asyncTransportConnection == null)
        {
            return false;
        }
        while (_xmodemRxQueue.TryTake(out _)) { }
        _lineOwner = _asyncTransportConnection;
        _isXModemActive = true;
        for (int i = start; i < count; i++)
        {
            _xmodemRxQueue.Add(buffer[i]);
        }
        _zmodemTail = new byte[0];
        try
        {
            BeginInvoke((Action)AskAndReceiveZModem);
        }
        catch (InvalidOperationException)
        {
            ReleaseZModemLine();
            return false;
        }
        return true;
    }

    private void AskAndReceiveZModem()
    {
        if (IsDisposed || _asyncTransportConnection == null || _connectionState != ConnectionStateEnum.Connected)
        {
            ReleaseZModemLine();
            return;
        }
        AsyncTransportConnection owner = _lineOwner;
        string folder = ChooseZModemFolder("The device wants to send files via ZMODEM." + Environment.NewLine
            + "Choose the folder to save them in, or press Cancel to refuse.");
        if (folder == null)
        {
            // Tell the sender to give up, and drop what it has sent in the meantime. The dialog pumps messages, so a
            // reconnect may have put a new connection in place: never send the cancel bytes to that new session.
            try
            {
                byte[] cancel = { 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08 };
                SendToConnection(owner, cancel, cancel.Length);
            }
            catch (InvalidOperationException)
            {
                // The connection is already gone.
            }
            ReleaseZModemLine();
            return;
        }
        // The dialog pumps messages, so the connection that asked for the files may have been replaced while the user
        // was choosing a folder: the queued bytes belong to the old session and the transfer must not start on the new one.
        if (!ReferenceEquals(owner, _asyncTransportConnection) || _connectionState != ConnectionStateEnum.Connected)
        {
            ReleaseZModemLine();
            return;
        }
        RunZModemReceive(folder);
    }

    /// <summary>Asks for the folder; null when the user cancels.</summary>
    private string ChooseZModemFolder(string description)
    {
        string start = _zmodemFolder;
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
        {
            start = _commonSettings?.DefaultDownloadDirectory;
        }
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            start = Directory.Exists(downloads) ? downloads : null;
        }
        string chosen = ThemedFolderBrowser.Browse(this, description, start);
        if (string.IsNullOrEmpty(chosen))
        {
            return null;
        }
        _zmodemFolder = chosen;
        return _zmodemFolder;
    }

    /// <summary>Gives the line back to the terminal and holds off automatic starts for a moment.</summary>
    private void ReleaseZModemLine()
    {
        _isXModemActive = false;
        _lineOwner = null;
        _cancelLineTransfer = null;
        while (_xmodemRxQueue.TryTake(out _)) { }
        _zmodemTail = new byte[0];
        _zmodemIgnoreUntil = Environment.TickCount + 3000;
        _zmodemIgnoring = true;
    }

    private ZModemTransfer.SendDataHandler CreateZModemSendHandler()
    {
        // The connection the transfer started on (the line is claimed before this is called): never the one that a
        // reconnect puts in its place.
        AsyncTransportConnection owner = _lineOwner ?? _asyncTransportConnection;
        return (data, offset, length) =>
        {
            // Writes are queued, so a failure of an earlier one is only known now: end the transfer with its reason.
            string writeError = owner?.WriteError;
            if (writeError != null)
            {
                throw new IOException(writeError);
            }
            byte[] copy = new byte[length];
            Array.Copy(data, offset, copy, 0, length);
            if (!SendToConnection(owner, copy, length))
            {
                throw new IOException("The connection is closed.");
            }
        };
    }

    // ---------- receiving ----------

    private void receiveFilesViaZModemToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before receiving files via ZMODEM.", "ZMODEM Receive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string folder = ChooseZModemFolder("Choose the folder to save the received files in." + Environment.NewLine
            + "Start the send on the device (for example: sz file) after pressing OK.");
        if (folder == null)
        {
            return;
        }
        // Claim the line before the first byte can arrive; stale bytes (a prompt, echo) must not reach the protocol.
        while (_xmodemRxQueue.TryTake(out _)) { }
        _lineOwner = _asyncTransportConnection;
        _isXModemActive = true;
        RunZModemReceive(folder);
    }

    /// <summary>Runs the receive on a worker thread with a progress window. The caller has claimed the line.</summary>
    private void RunZModemReceive(string folder)
    {
        ITransferProgressWindow transferForm = CreateTransferProgressWindow("ZMODEM Receive", showErrorCount: true);
        try
        {
            transferForm.Show();
        }
        catch (Exception)
        {
            transferForm.Dispose();
            ReleaseZModemLine();
            throw;
        }
        _cancelLineTransfer = transferForm.Cancel;

        ZModemOptions options = new ZModemOptions { EscapeControlCharacters = escapeZModemControlToolStripMenuItem.Checked };
        Dictionary<ZModemFileInfo, string> paths = new Dictionary<ZModemFileInfo, string>();
        List<string> saved = new List<string>();

        ThreadPool.QueueUserWorkItem(state =>
        {
            bool success = false;
            string error = null;
            try
            {
                ZModemTransfer.ProgressHandler progress = (done, total, errors, status) =>
                    transferForm.UpdateProgress(done, total, errors, status);
                success = ZModemTransfer.ReceiveFiles(
                    info => CreateReceivedFile(folder, info, paths),
                    (info, complete) => FinishReceivedFile(info, complete, paths, saved),
                    CreateZModemSendHandler(), ReadXModemByte, transferForm.CancellationToken, progress, options);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                transferForm.UpdateProgress(0, 0, 1, "Error: " + ex.Message);
            }
            finally
            {
                ReleaseZModemLine();
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    bool cancelled = transferForm.IsCancelled;
                    string lastStatus = transferForm.LastStatus;
                    transferForm.Dispose();
                    string title = "ZMODEM Receive";
                    if (success)
                    {
                        ThemedMessageBox.Show(DescribeSavedFiles(saved, folder), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (!cancelled)
                    {
                        string reason = error ?? lastStatus;
                        string text = "ZMODEM receive failed." + (string.IsNullOrEmpty(reason) ? string.Empty : "\r\n\r\n" + reason);
                        if (saved.Count > 0)
                        {
                            text += "\r\n\r\nFiles received completely before the failure:\r\n" + string.Join("\r\n", saved);
                        }
                        ThemedMessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // The terminal was closed while the transfer was running: nobody is left to show the result to.
            }
        });
    }

    private static string DescribeSavedFiles(List<string> saved, string folder)
    {
        if (saved.Count == 0)
        {
            return "ZMODEM receive completed. The sender did not send any file.";
        }
        StringBuilder text = new StringBuilder();
        text.Append(saved.Count == 1 ? "ZMODEM receive completed: 1 file saved to " : "ZMODEM receive completed: " + saved.Count + " files saved to ");
        text.Append(folder).Append(":\r\n");
        for (int i = 0; i < saved.Count && i < 10; i++)
        {
            text.Append("\r\n").Append(Path.GetFileName(saved[i]));
        }
        if (saved.Count > 10)
        {
            text.Append("\r\n...");
        }
        return text.ToString();
    }

    /// <summary>
    /// Creates the file for one announced file. The name has been made safe by the protocol code (no directories, no reserved
    /// characters); an existing file is never overwritten, the new one gets " (1)", " (2)"... added to its name.
    /// </summary>
    private static Stream CreateReceivedFile(string folder, ZModemFileInfo info, Dictionary<ZModemFileInfo, string> paths)
    {
        string fullFolder = Path.GetFullPath(folder);
        string stem = Path.GetFileNameWithoutExtension(info.Name);
        string extension = Path.GetExtension(info.Name);
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            string name = attempt == 0 ? info.Name : stem + " (" + attempt + ")" + extension;
            string path = Path.GetFullPath(Path.Combine(fullFolder, name));
            // Belt and braces: whatever the name was, the file must land directly in the chosen folder.
            if (!string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\', '/'), fullFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Refused to save a file with an unsafe name: " + info.RemoteName);
            }
            try
            {
                FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                paths[info] = path;
                return stream;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Taken: try the next name.
            }
        }
        throw new IOException("Cannot find a free name for " + info.Name + ".");
    }

    /// <summary>A complete file gets the time stamp the sender gave it; the remains of an incomplete one are removed.</summary>
    private static void FinishReceivedFile(ZModemFileInfo info, bool complete, Dictionary<ZModemFileInfo, string> paths, List<string> saved)
    {
        if (!paths.TryGetValue(info, out string path))
        {
            return;
        }
        paths.Remove(info);
        if (complete)
        {
            if (info.ModifiedUtc.HasValue)
            {
                try
                {
                    File.SetLastWriteTimeUtc(path, info.ModifiedUtc.Value);
                }
                catch (Exception)
                {
                    // The file keeps the time of its arrival.
                }
            }
            saved.Add(path);
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Nothing more can be done about a leftover.
        }
    }

    // ---------- sending ----------

    private void sendFilesViaZModemToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (IsLineBusy())
        {
            return;
        }
        if (_connectionState != ConnectionStateEnum.Connected || _asyncTransportConnection == null)
        {
            ThemedMessageBox.Show("Please connect first before sending files via ZMODEM.", "ZMODEM Send", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "Select files to send via ZMODEM (at a shell prompt the device's rz is started automatically)",
            Filter = "All files (*.*)|*.*",
            Multiselect = true
        };
        if (openFileDialog.ShowDialog(this) != DialogResult.OK || openFileDialog.FileNames.Length == 0)
        {
            return;
        }
        // The streams are opened later, on the worker thread, when the "using" dialog above is already disposed.
        StartZModemSend((string[])openFileDialog.FileNames.Clone());
    }

    /// <summary>Must be called on the UI thread: the line is claimed here, before the first byte can arrive.</summary>
    private void StartZModemSend(string[] fileNames)
    {
        if (IsLineClaimed)
        {
            return;
        }

        ITransferProgressWindow transferForm = CreateTransferProgressWindow("ZMODEM Send", showErrorCount: true);
        transferForm.Show();

        while (_xmodemRxQueue.TryTake(out _)) { }
        _lineOwner = _asyncTransportConnection;
        _cancelLineTransfer = transferForm.Cancel;
        _isXModemActive = true;
        ZModemOptions options = new ZModemOptions { EscapeControlCharacters = escapeZModemControlToolStripMenuItem.Checked };

        ThreadPool.QueueUserWorkItem(state =>
        {
            bool success = false;
            string error = null;
            List<FileStream> opened = new List<FileStream>();
            try
            {
                List<ZModemSendItem> items = new List<ZModemSendItem>();
                foreach (string fileName in fileNames)
                {
                    FileInfo info = new FileInfo(fileName);
                    FileStream stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                    opened.Add(stream);
                    items.Add(new ZModemSendItem(info.Name, stream, info.Length, info.LastWriteTimeUtc));
                }
                ZModemTransfer.ProgressHandler progress = (done, total, errors, status) =>
                    transferForm.UpdateProgress(done, total, errors, status);
                success = ZModemTransfer.SendFiles(items, CreateZModemSendHandler(), ReadXModemByte, transferForm.CancellationToken, progress, options);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                transferForm.UpdateProgress(0, 0, 1, "Error: " + ex.Message);
            }
            finally
            {
                foreach (FileStream stream in opened)
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception)
                    {
                        // Only read from; nothing is lost.
                    }
                }
                ReleaseZModemLine();
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    bool cancelled = transferForm.IsCancelled;
                    string lastStatus = transferForm.LastStatus;
                    transferForm.Dispose();
                    string title = "ZMODEM Send";
                    if (success)
                    {
                        ThemedMessageBox.Show(fileNames.Length == 1 ? "ZMODEM send completed successfully." : "ZMODEM send of " + fileNames.Length + " files completed successfully.",
                            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (!cancelled)
                    {
                        string reason = error ?? lastStatus;
                        ThemedMessageBox.Show("ZMODEM send failed." + (string.IsNullOrEmpty(reason) ? string.Empty : "\r\n\r\n" + reason),
                            title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // The terminal was closed while the transfer was running: nobody is left to show the result to.
            }
        });
    }
}
