using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Sync;

internal class FileByFileSyncEngine : CommandSyncEngine
{
    private class FileByFileStream : IRemoteFileStream, IDisposable
    {
        private readonly ScpDownloadStream _stream;

        private FileByFileSyncEngine _engine;

        private DownloadEntry _entry;

        public int ErrorCode { get; }

        public long ModifiedTime => new LocalFileSystem.FileStamp(0L, _entry.ModifiedSeconds).LastWriteFileTime;

        public FileByFileStream(FileByFileSyncEngine fileByFileSyncEngine, DownloadEntry downloadEntry)
        {
            _engine = fileByFileSyncEngine;
            this._entry = downloadEntry;
            try
            {
                _stream = _engine._session.OpenDownload(this._entry.RemotePath);
            }
            catch
            {
                this.ErrorCode = -1;
            }
        }

        public void Dispose()
        {
            _stream?.Close();
        }

        public int ReadChunk(ref byte[] buffer)
        {
            if (buffer == null || buffer.Length < _engine.BufferSize)
            {
                buffer = new byte[_engine.BufferSize];
            }
            if (ErrorCode != 0 || _stream == null)
            {
                return 0;
            }
            return _stream.Read(buffer, 0, buffer.Length);
        }
    }

    private class FileByFileSource : IRemoteFileSource, IDisposable
    {
        private FileByFileSyncEngine _engine;

        private List<DownloadEntry> _entries;

        public FileByFileSource(FileByFileSyncEngine fileByFileSyncEngine, List<DownloadEntry> entries)
        {
            this._engine = fileByFileSyncEngine;
            this._entries = entries;
        }

        public IRemoteFileStream OpenFile(int fileId)
        {
            if (fileId < 0 || fileId >= _entries.Count)
            {
                throw new ArgumentException("uniqueFileID");
            }
            return new FileByFileStream(this._engine, _entries[fileId]);
        }

        public void Dispose()
        {
        }
    }

    private struct UploadedFile
    {
        public long Size;

        public string Path;
    }

    public int BufferSize => base.Tuning.BufferSize;

    public FileByFileSyncEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, SyncTuning syncTuning, ISyncCustomization syncCustomization)
        : base(sshSession, syncSettings, syncEvents, localFileCache, syncTuning, syncCustomization)
    {
    }

    public override string ToString()
    {
        return "File-by-file synchronization engine";
    }

    public override IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> actions)
    {
        if (DownloadList == null)
        {
            throw new Exception("Remote file table is not defined");
        }
        return new FileByFileSource(this, DownloadList);
    }

    public override void Upload(List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics)
    {
        UploadedFile[] uploadedFiles = new UploadedFile[localPaths.Count];
        CreateDirectories(actions, uploadedFiles);
        byte[] copyBuffer = new byte[this.BufferSize];
        long totalTransferred = 0L;
        for (int i = 0; i < localPaths.Count; i++)
        {
            if (uploadedFiles[i].Path == null)
            {
                continue;
            }
            QuickSyncWarningType warningType = QuickSyncWarningType.CannotReadLocalFile;
            try
            {
                using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(localPaths[i], LocalFileSystem.FileOpenMode.Open);
                uploadedFiles[i].Size = nativeFile.GetLastWriteTime();
                warningType = QuickSyncWarningType.CannotCreateTargetFile;
                using ScpChannelStream scpChannelStream = _session.OpenUpload(uploadedFiles[i].Path, nativeFile.GetSize());
                while (true)
                {
                    int bytesRead = nativeFile.Read(copyBuffer, copyBuffer.Length);
                    if (bytesRead != 0)
                    {
                        scpChannelStream.Write(copyBuffer, 0, bytesRead);
                        totalTransferred += bytesRead;
                        base._events?.OnProgress(new SyncProgress
                        {
                            Stage = SyncStage.Transferring,
                            CurrentFile = uploadedFiles[i].Path,
                            Transferred = totalTransferred,
                            Total = transferStatistics.TotalBytes
                        });
                        continue;
                    }
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                base._events.OnWarning(new SyncWarning
                {
                    Exception = exception,
                    Warning = new QuickSyncWarning(warningType, uploadedFiles[i].Path, 0)
                });
            }
        }
        ApplyAttributes(actions, uploadedFiles, localPaths);
    }

    private void CreateDirectories(List<SyncPlan.SyncAction> actions, UploadedFile[] uploadedFiles)
    {
        string marker = Guid.NewGuid().ToString();
        MemoryStream memoryStream = new MemoryStream();
        string mainDirMsg = "--- Entered main directory ---";
        string currentRemoteDir = _settings.RemotePath;
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + marker);
        streamWriter.WriteLine("mkdir -p \"" + _settings.RemotePath + "\"");
        streamWriter.WriteLine("test -d \"" + _settings.RemotePath + "\" || exit 1");
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        streamWriter.WriteLine("echo " + mainDirMsg);
        for (int i = 0; i < actions.Count; i++)
        {
            SyncPlan.SyncAction action = actions[i];
            switch (action.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                    streamWriter.WriteLine($"mkdir \"{action.Path}\" || echo \"cmdfail:{i}\"");
                    goto case SyncPlan.ActionType.EnterDirectory;
                case SyncPlan.ActionType.EnterDirectory:
                    currentRemoteDir = RemotePathUtils.Combine(currentRemoteDir, action.Path);
                    streamWriter.WriteLine($"cd \"{currentRemoteDir}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    currentRemoteDir = RemotePathUtils.GetDirectoryName(currentRemoteDir);
                    streamWriter.WriteLine("cd \"" + currentRemoteDir + "\"");
                    break;
                case SyncPlan.ActionType.TransferFile:
                    uploadedFiles[action.Argument].Path = RemotePathUtils.Combine(currentRemoteDir, action.Path);
                    break;
                case SyncPlan.ActionType.DeleteTargetFile:
                    streamWriter.WriteLine($"rm -f \"{action.Path}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                    streamWriter.WriteLine($"rm -f \"{action.Path}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.DeleteTargetDirectoryRecursively:
                    streamWriter.WriteLine($"rm -rf \"{action.Path}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.PrintStderrMessage:
                    streamWriter.WriteLine($"echo \"cmdfail:{i}\"");
                    break;
            }
        }
        streamWriter.Flush();
        List<string> scriptOutput = RunScript(marker, memoryStream.ToArray());
        bool markerSeen = false;
        bool mainDirSeen = false;
        foreach (string line in scriptOutput)
        {
            if (!markerSeen)
            {
                if (!(line != marker))
                {
                    markerSeen = true;
                }
                continue;
            }
            if (line == mainDirMsg)
            {
                mainDirSeen = true;
            }
            if (line.StartsWith("cmdfail:") && int.TryParse(line.Substring(8), out var actionIdx) && actionIdx >= 0 && actionIdx < actions.Count)
            {
                _events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(SysprogsSyncEngine.GetWarningType(actions[actionIdx].Type), actions[actionIdx].Path, -1)
                });
            }
        }
        if (!mainDirSeen)
        {
            throw new Exception("Failed to create " + _settings.RemotePath);
        }
    }

    private void ApplyAttributes(List<SyncPlan.SyncAction> actions, UploadedFile[] uploadedFiles, List<LocalPath> localPaths)
    {
        string marker = Guid.NewGuid().ToString();
        MemoryStream memoryStream = new MemoryStream();
        string currentRemoteDir = _settings.RemotePath;
        IPermissionProvider permissionProvider = base._events?.PermissionProvider;
        int defaultPermissions = Convert.ToInt32("755", 8);
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + marker);
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        for (int i = 0; i < actions.Count; i++)
        {
            SyncPlan.SyncAction action = actions[i];
            switch (action.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                case SyncPlan.ActionType.EnterDirectory:
                    currentRemoteDir = RemotePathUtils.Combine(currentRemoteDir, action.Path);
                    streamWriter.WriteLine($"cd \"{currentRemoteDir}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    currentRemoteDir = RemotePathUtils.GetDirectoryName(currentRemoteDir);
                    streamWriter.WriteLine("cd \"" + currentRemoteDir + "\"");
                    break;
                case SyncPlan.ActionType.TransferFile:
                    {
                        uploadedFiles[action.Argument].Path = RemotePathUtils.Combine(currentRemoteDir, action.Path);
                        int permissions = permissionProvider?.GetFilePermissions(localPaths[action.Argument]) ?? defaultPermissions;
                        streamWriter.WriteLine("chmod " + Convert.ToString(permissions, 8) + " \"" + action.Path + "\"");
                        streamWriter.WriteLine($"touch -d @{new LocalFileSystem.FileStamp(0L, uploadedFiles[action.Argument].Size + _settings.ClockOffset.Seconds).UnixSeconds} \"{action.Path}\"");
                        break;
                    }
                case SyncPlan.ActionType.CreateRemoteSymlink:
                    {
                        KeyValuePair<string, string> symlinkPair = StringUtils.SplitAtFirst(action.Path, '\n');
                        string linkFileName = RemotePathUtils.GetFileName(symlinkPair.Key);
                        if (symlinkPair.Key == null || symlinkPair.Value == null)
                        {
                            throw new Exception("Invalid queued symlink: " + action.Path);
                        }
                        streamWriter.WriteLine("rm -f \"" + linkFileName + "\"");
                        streamWriter.WriteLine($"ln -s \"{symlinkPair.Value}\" \"{linkFileName}\" || echo \"cmdfail:{i}\"");
                        break;
                    }
            }
        }
        streamWriter.Flush();
        List<string> scriptOutput = RunScript(marker, memoryStream.ToArray());
        bool markerSeen = false;
        foreach (string line in scriptOutput)
        {
            int actionIdx;
            if (!markerSeen)
            {
                if (!(line != marker))
                {
                    markerSeen = true;
                }
            }
            else if (line.StartsWith("cmdfail:") && int.TryParse(line.Substring(8), out actionIdx) && actionIdx >= 0 && actionIdx < actions.Count)
            {
                base._events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(SysprogsSyncEngine.GetWarningType(actions[actionIdx].Type), actions[actionIdx].Path, -1)
                });
            }
        }
    }
}
