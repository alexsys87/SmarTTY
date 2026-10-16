using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
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

        public FileByFileSource(FileByFileSyncEngine fileByFileSyncEngine, List<DownloadEntry> list)
        {
            this._engine = fileByFileSyncEngine;
            this._entries = list;
        }

        public IRemoteFileStream OpenFile(int value2)
        {
            if (value2 < 0 || value2 >= _entries.Count)
            {
                throw new ArgumentException("uniqueFileID");
            }
            return new FileByFileStream(this._engine, _entries[value2]);
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

    public override IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> items)
    {
        if (DownloadList == null)
        {
            throw new Exception("Remote file table is not defined");
        }
        return new FileByFileSource(this, DownloadList);
    }

    public override void Upload(List<SyncPlan.SyncAction> list, List<LocalPath> list2, ref TransferStatistics transferStatistics)
    {
        UploadedFile[] array = new UploadedFile[list2.Count];
        CreateDirectories(list, array);
        byte[] array2 = new byte[this.BufferSize];
        long num = 0L;
        for (int num2 = 0; num2 < list2.Count; num2++)
        {
            if (array[num2].Path == null)
            {
                continue;
            }
            QuickSyncWarningType quickSyncWarningType = QuickSyncWarningType.CannotReadLocalFile;
            try
            {
                using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(list2[num2], LocalFileSystem.FileOpenMode.Open);
                array[num2].Size = nativeFile.GetLastWriteTime();
                quickSyncWarningType = QuickSyncWarningType.CannotCreateTargetFile;
                using ScpChannelStream scpChannelStream = _session.OpenUpload(array[num2].Path, nativeFile.GetSize());
                while (true)
                {
                    int num3 = nativeFile.Read(array2, array2.Length);
                    if (num3 != 0)
                    {
                        scpChannelStream.Write(array2, 0, num3);
                        num += num3;
                        base._events?.OnProgress(new SyncProgress
                        {
                            Stage = SyncStage.Transferring,
                            CurrentFile = array[num2].Path,
                            Transferred = num,
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
            catch (Exception ex2)
            {
                base._events.OnWarning(new SyncWarning
                {
                    Exception = ex2,
                    Warning = new QuickSyncWarning(quickSyncWarningType, array[num2].Path, 0)
                });
            }
        }
        ApplyAttributes(list, array, list2);
    }

    private void CreateDirectories(List<SyncPlan.SyncAction> list2, UploadedFile[] items)
    {
        string text = Guid.NewGuid().ToString();
        MemoryStream memoryStream = new MemoryStream();
        string text2 = "--- Entered main directory ---";
        string text3 = _settings.RemotePath;
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + text);
        streamWriter.WriteLine("mkdir -p \"" + _settings.RemotePath + "\"");
        streamWriter.WriteLine("test -d \"" + _settings.RemotePath + "\" || exit 1");
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        streamWriter.WriteLine("echo " + text2);
        for (int num = 0; num < list2.Count; num++)
        {
            SyncPlan.SyncAction syncAction = list2[num];
            switch (syncAction.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                    streamWriter.WriteLine($"mkdir \"{syncAction.Path}\" || echo \"cmdfail:{num}\"");
                    goto case SyncPlan.ActionType.EnterDirectory;
                case SyncPlan.ActionType.EnterDirectory:
                    text3 = RemotePathUtils.Combine(text3, syncAction.Path);
                    streamWriter.WriteLine($"cd \"{text3}\" || echo \"cmdfail:{num}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    text3 = RemotePathUtils.GetDirectoryName(text3);
                    streamWriter.WriteLine("cd \"" + text3 + "\"");
                    break;
                case SyncPlan.ActionType.TransferFile:
                    items[syncAction.Argument].Path = RemotePathUtils.Combine(text3, syncAction.Path);
                    break;
                case SyncPlan.ActionType.DeleteTargetFile:
                    streamWriter.WriteLine($"rm -f \"{syncAction.Path}\" || echo \"cmdfail:{num}\"");
                    break;
                case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                    streamWriter.WriteLine($"rm -f \"{syncAction.Path}\" || echo \"cmdfail:{num}\"");
                    break;
                case SyncPlan.ActionType.DeleteTargetDirectoryRecursively:
                    streamWriter.WriteLine($"rm -rf \"{syncAction.Path}\" || echo \"cmdfail:{num}\"");
                    break;
                case SyncPlan.ActionType.PrintStderrMessage:
                    streamWriter.WriteLine($"echo \"cmdfail:{num}\"");
                    break;
            }
        }
        streamWriter.Flush();
        List<string> list = RunScript(text, memoryStream.ToArray());
        bool flag = false;
        bool flag2 = false;
        foreach (string item in list)
        {
            if (!flag)
            {
                if (!(item != text))
                {
                    flag = true;
                }
                continue;
            }
            if (item == text2)
            {
                flag2 = true;
            }
            if (item.StartsWith("cmdfail:") && int.TryParse(item.Substring(8), out var result) && result >= 0 && result < list2.Count)
            {
                _events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(SysprogsSyncEngine.GetWarningType(list2[result].Type), list2[result].Path, -1)
                });
            }
        }
        if (!flag2)
        {
            throw new Exception("Failed to create " + _settings.RemotePath);
        }
    }

    private void ApplyAttributes(List<SyncPlan.SyncAction> list2, UploadedFile[] items, List<LocalPath> list3)
    {
        string text = Guid.NewGuid().ToString();
        MemoryStream memoryStream = new MemoryStream();
        string text2 = _settings.RemotePath;
        IPermissionProvider permissionProvider = base._events?.PermissionProvider;
        int num = Convert.ToInt32("755", 8);
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + text);
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        for (int num2 = 0; num2 < list2.Count; num2++)
        {
            SyncPlan.SyncAction syncAction = list2[num2];
            switch (syncAction.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                case SyncPlan.ActionType.EnterDirectory:
                    text2 = RemotePathUtils.Combine(text2, syncAction.Path);
                    streamWriter.WriteLine($"cd \"{text2}\" || echo \"cmdfail:{num2}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    text2 = RemotePathUtils.GetDirectoryName(text2);
                    streamWriter.WriteLine("cd \"" + text2 + "\"");
                    break;
                case SyncPlan.ActionType.TransferFile:
                    {
                        items[syncAction.Argument].Path = RemotePathUtils.Combine(text2, syncAction.Path);
                        int num3 = permissionProvider?.GetFilePermissions(list3[syncAction.Argument]) ?? num;
                        streamWriter.WriteLine("chmod " + Convert.ToString(num3, 8) + " \"" + syncAction.Path + "\"");
                        streamWriter.WriteLine($"touch -d @{new LocalFileSystem.FileStamp(0L, items[syncAction.Argument].Size + _settings.ClockOffset.Seconds).UnixSeconds} \"{syncAction.Path}\"");
                        break;
                    }
                case SyncPlan.ActionType.CreateRemoteSymlink:
                    {
                        KeyValuePair<string, string> keyValuePair = StringUtils.SplitAtFirst(syncAction.Path, '\n');
                        string text3 = RemotePathUtils.GetFileName(keyValuePair.Key);
                        if (keyValuePair.Key == null || keyValuePair.Value == null)
                        {
                            throw new Exception("Invalid queued symlink: " + syncAction.Path);
                        }
                        streamWriter.WriteLine("rm -f \"" + text3 + "\"");
                        streamWriter.WriteLine($"ln -s \"{keyValuePair.Value}\" \"{text3}\" || echo \"cmdfail:{num2}\"");
                        break;
                    }
            }
        }
        streamWriter.Flush();
        List<string> list = RunScript(text, memoryStream.ToArray());
        bool flag = false;
        foreach (string item in list)
        {
            int result;
            if (!flag)
            {
                if (!(item != text))
                {
                    flag = true;
                }
            }
            else if (item.StartsWith("cmdfail:") && int.TryParse(item.Substring(8), out result) && result >= 0 && result < list2.Count)
            {
                base._events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(SysprogsSyncEngine.GetWarningType(list2[result].Type), list2[result].Path, -1)
                });
            }
        }
    }
}
