using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace SmarTTY.Transport;

/// <summary>
/// Saves the unfinished transfers of a connection (queued, running or failed) so that they come back the next time
/// SmarTTY connects to the same server, and can resume from what was transferred.
/// </summary>
public static class TransferQueueStore
{
    public class SavedTransfer
    {
        public Guid Id;

        public bool IsDownload;

        public string RemotePath;

        public string LocalPath;

        public bool OpenFolderWhenDone;

        /// <summary>A directory downloaded as a .tar.gz archive (see <see cref="FileTransferManager.TransferJob.IsArchive"/>).</summary>
        public bool IsArchive;

        /// <summary>
        /// True if the transfer was queued or running, or was cut short by a lost connection (it continues by itself),
        /// false if it had failed.
        /// </summary>
        public bool WasActive;

        public string ErrorMessage;

        public long TotalBytes;

        public long TransferredBytes;

        public long RemoteSize = -1;

        public DateTime RemoteModifyTime;

        public long LocalSize = -1;

        public DateTime LocalModifyTime;

        public string RemoteTempPath;
    }

    public class SavedQueue
    {
        public List<SavedTransfer> Transfers = new List<SavedTransfer>();

        /// <summary>
        /// The fingerprint of the host key of the server the queue belongs to. The file is named after user, host and port, which many
        /// devices share (root@192.168.1.1): without it a queue saved for one router would start by itself on another one.
        /// Empty in a file written by an earlier version.
        /// </summary>
        public string HostKey;
    }

    /// <summary>The text under which the queue file remembers a server, or null when the key is not known.</summary>
    public static string HostKeyId(byte[] hostKeyBlob)
    {
        if (hostKeyBlob == null || hostKeyBlob.Length == 0)
        {
            return null;
        }
        using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
        {
            return Convert.ToBase64String(hash.ComputeHash(hostKeyBlob));
        }
    }

    /// <summary>
    /// The file for one connection: the name is built from user, host and port with unsafe characters replaced.
    /// </summary>
    public static string GetFilePath(string directory, string user, string host, int port)
    {
        StringBuilder name = new StringBuilder();
        foreach (char c in (user ?? "") + "@" + (host ?? "") + "_" + port)
        {
            name.Append((char.IsLetterOrDigit(c) || c == '@' || c == '.' || c == '-' || c == '_') ? c : '_');
        }
        return Path.Combine(directory, name + ".xml");
    }

    public static void Save(string path, IEnumerable<FileTransferManager.TransferJob> jobs, string hostKey = null)
    {
        SavedQueue queue = new SavedQueue { HostKey = hostKey };
        foreach (FileTransferManager.TransferJob job in jobs)
        {
            FileTransferManager.TransferState state = job.State;
            if (state == FileTransferManager.TransferState.Completed || state == FileTransferManager.TransferState.Cancelled)
            {
                continue;
            }
            queue.Transfers.Add(new SavedTransfer
            {
                Id = job.Id,
                IsDownload = job.IsDownload,
                RemotePath = job.RemotePath,
                LocalPath = job.LocalPath,
                OpenFolderWhenDone = job.OpenFolderWhenDone,
                IsArchive = job.IsArchive,
                // A transfer that failed only because the connection went away continues by itself, like a queued one: closing
                // the application during the loss must not turn it into one that has to be started by hand.
                WasActive = state != FileTransferManager.TransferState.Failed || job.InterruptedByConnectionLoss,
                ErrorMessage = job.ErrorMessage,
                TotalBytes = job.TotalBytes,
                TransferredBytes = job.TransferredBytes,
                RemoteSize = job.RemoteSize,
                RemoteModifyTime = job.RemoteModifyTime,
                LocalSize = job.LocalSize,
                LocalModifyTime = job.LocalModifyTime,
                RemoteTempPath = job.RemoteTempPath
            });
        }
        if (queue.Transfers.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // Written to a temporary file first: closing SmarTTY in the middle of a save must not lose the queue.
        string temp = path + ".tmp";
        using (FileStream stream = File.Create(temp))
        {
            new XmlSerializer(typeof(SavedQueue)).Serialize(stream, queue);
        }
        // Replaced in one step: with a delete before the move, a crash between the two would leave only the temporary file, which
        // Load does not read.
        if (File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, null);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
            {
                // A file system without the replace call: the plain way below.
            }
            File.Delete(path);
        }
        File.Move(temp, path);
    }

    /// <summary>
    /// Loads the saved transfers as jobs: those that were queued or running are in the Queued state, the failed ones
    /// in the Failed state. Returns an empty list if there is no (readable) file.
    /// </summary>
    public static List<FileTransferManager.TransferJob> Load(string path)
    {
        return Load(path, null);
    }

    /// <summary>
    /// Like <see cref="Load(string)"/>, and when the queue was saved for a server with another host key than
    /// <paramref name="currentHostKey"/> (see <see cref="HostKeyId"/>), none of its transfers is queued: they come back as failed ones,
    /// to be started by hand, because the same user@host:port may be another device (an upload would replace its files).
    /// </summary>
    public static List<FileTransferManager.TransferJob> Load(string path, string currentHostKey)
    {
        if (!File.Exists(path))
        {
            return new List<FileTransferManager.TransferJob>();
        }
        SavedQueue queue;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            queue = (SavedQueue)new XmlSerializer(typeof(SavedQueue)).Deserialize(reader);
        }
        catch
        {
            return new List<FileTransferManager.TransferJob>();
        }
        bool otherServer = !string.IsNullOrEmpty(queue.HostKey) && !string.IsNullOrEmpty(currentHostKey) && queue.HostKey != currentHostKey;
        return queue.Transfers.Where((SavedTransfer saved) => !string.IsNullOrEmpty(saved.RemotePath) && !string.IsNullOrEmpty(saved.LocalPath)).Select((SavedTransfer saved) => new FileTransferManager.TransferJob
        {
            Id = saved.Id,
            IsDownload = saved.IsDownload,
            RemotePath = saved.RemotePath,
            LocalPath = saved.LocalPath,
            OpenFolderWhenDone = saved.OpenFolderWhenDone,
            IsArchive = saved.IsArchive,
            State = saved.WasActive && !otherServer ? FileTransferManager.TransferState.Queued : FileTransferManager.TransferState.Failed,
            ErrorMessage = otherServer
                ? "Not started: this transfer was saved for a server with another host key (a different device with the same address?). Retry it if it is meant for this one."
                : saved.WasActive ? null : saved.ErrorMessage,
            TotalBytes = saved.TotalBytes,
            TransferredBytes = saved.TransferredBytes,
            RemoteSize = saved.RemoteSize,
            RemoteModifyTime = saved.RemoteModifyTime,
            LocalSize = saved.LocalSize,
            LocalModifyTime = saved.LocalModifyTime,
            RemoteTempPath = saved.RemoteTempPath
        }).ToList();
    }
}
