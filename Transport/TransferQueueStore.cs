using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

    public static void Save(string path, IEnumerable<FileTransferManager.TransferJob> jobs)
    {
        SavedQueue queue = new SavedQueue();
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
        if (File.Exists(path))
        {
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
        if (!File.Exists(path))
        {
            return new List<FileTransferManager.TransferJob>();
        }
        SavedQueue queue;
        try
        {
            using FileStream stream = File.OpenRead(path);
            queue = (SavedQueue)new XmlSerializer(typeof(SavedQueue)).Deserialize(stream);
        }
        catch
        {
            return new List<FileTransferManager.TransferJob>();
        }
        return queue.Transfers.Where((SavedTransfer saved) => !string.IsNullOrEmpty(saved.RemotePath) && !string.IsNullOrEmpty(saved.LocalPath)).Select((SavedTransfer saved) => new FileTransferManager.TransferJob
        {
            Id = saved.Id,
            IsDownload = saved.IsDownload,
            RemotePath = saved.RemotePath,
            LocalPath = saved.LocalPath,
            OpenFolderWhenDone = saved.OpenFolderWhenDone,
            IsArchive = saved.IsArchive,
            State = saved.WasActive ? FileTransferManager.TransferState.Queued : FileTransferManager.TransferState.Failed,
            ErrorMessage = saved.WasActive ? null : saved.ErrorMessage,
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
