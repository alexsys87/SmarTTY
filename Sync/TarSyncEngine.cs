using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
using SmarTTY.IO;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;

namespace SmarTTY.Sync;

internal class TarSyncEngine : CommandSyncEngine
{
    private class StderrMonitor
    {
        private LineSplitter _lineSplitter = new LineSplitter();

        public bool HasOutput { get; private set; }

        public event Action<string> StderrLine;

        public StderrMonitor(TarSyncEngine tarSyncEngine, SshShellChannel sshShellChannel)
        {
            sshShellChannel.DataReceived += OnDataReceived;
            sshShellChannel.Finished += OnFinished;
            sshShellChannel.PollingEnabled = true;
        }

        private void OnFinished(object sender)
        {
        }

        internal void OnDataReceived(object sender, byte[] buffer, int offset, int count, int dataType)
        {
            try
            {
                if (dataType != 1)
                {
                    return;
                }
                foreach (string line in this._lineSplitter.Feed(buffer, offset, count))
                {
                    HasOutput = true;
                    StderrLine?.Invoke(line);
                }
            }
            catch
            {
            }
        }
    }

    private struct TarPayload(string path, byte[] data)
    {
        public readonly string Path = path;

        public readonly byte[] Data = data;
    }

    public TarSyncEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, SyncTuning syncTuning, ISyncCustomization syncCustomization)
        : base(sshSession, syncSettings, syncEvents, localFileCache, syncTuning, syncCustomization)
    {
    }

    public override string ToString()
    {
        return "TAR synchronization engine";
    }

    public override IRemoteFileSource BeginDownload(IEnumerable<SyncPlan.SyncAction> items)
    {
        throw new NotSupportedException("Incoming sync transfers via tar are not yet supported");
    }

    public override void Upload(List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics)
    {
        string command = "LANG=en_US " + DirectoryCopier.TarCommands.GetExtractCommand(_settings.RemotePath, _settings.ClockOffset.IsKnown);
        IShellLauncher shellLauncher = null;
        if ((_settings.Options & SyncOptions.UseLauncher) != 0)
        {
            shellLauncher = _customization?.GetLauncher(command);
        }
        LocalFileSystem.FileStamp[] fileStamps = new LocalFileSystem.FileStamp[localPaths.Count];
        if (_session.RunCommandChecked("mkdir -p \"" + _settings.RemotePath + "\"", requireSuccess: true) == null)
        {
            throw new Exception("Failed to create " + _settings.RemotePath);
        }
        TarPayload tarPayload;
        using (SshShellChannel sshShellChannel = _session.OpenShellChannel(shellLauncher?.Command ?? command))
        {
            shellLauncher?.Prepare(sshShellChannel, "Outgoing file transfers");
            StderrMonitor stderrMonitor = new StderrMonitor(this, sshShellChannel);
            sshShellChannel.PollingEnabled = true;
            stderrMonitor.StderrLine += (string stderrLine) =>
            {
                base._events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, stderrLine, 0)
                });
            };
            tarPayload = this.WriteArchive(new TarWriter(null), sshShellChannel.Input, actions, localPaths, ref transferStatistics, fileStamps);
            sshShellChannel.Input.Write(new byte[1024], 0, 1024);
            sshShellChannel.SendEof();
            sshShellChannel.WaitEof();
            if (!stderrMonitor.HasOutput && sshShellChannel.ExitStatus == 0 && _cache != null)
            {
                for (int i = 0; i < localPaths.Count; i++)
                {
                    if (fileStamps[i].HasTimestamp)
                    {
                        _cache.StoreFileStamp(i, fileStamps[i]);
                    }
                }
            }
        }
        if (tarPayload.Data == null)
        {
            return;
        }
        List<string> scriptOutput = RunScript(tarPayload.Path, tarPayload.Data);
        bool markerSeen = false;
        foreach (string line in scriptOutput)
        {
            int actionIndex;
            if (!markerSeen)
            {
                if (!(line != tarPayload.Path))
                {
                    markerSeen = true;
                }
            }
            else if (line.StartsWith("cmdfail:") && int.TryParse(line.Substring(8), out actionIndex) && actionIndex >= 0 && actionIndex < actions.Count)
            {
                base._events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(SysprogsSyncEngine.GetWarningType(actions[actionIndex].Type), actions[actionIndex].Path, -1)
                });
            }
        }
    }

    private TarPayload WriteArchive(TarWriter tarWriter, Stream destinationStream, List<SyncPlan.SyncAction> actions, List<LocalPath> localPaths, ref TransferStatistics transferStatistics, LocalFileSystem.FileStamp[] fileStamps)
    {
        string marker = Guid.NewGuid().ToString();
        byte[] copyBuffer = new byte[Tuning.BufferSize];
        MemoryStream memoryStream = new MemoryStream();
        string remoteDir = _settings.RemotePath;
        RelativeRemotePath relativeRemotePath = default;
        byte[] paddingBuffer = new byte[1024];
        IPermissionProvider permissionProvider = base._events?.PermissionProvider;
        int defaultPermissions = Convert.ToInt32("755", 8);
        int scriptCommandCount = 0;
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + marker);
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        for (int i = 0; i < actions.Count; i++)
        {
            SyncPlan.SyncAction action = actions[i];
            int paddingBytes;
            switch (action.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                    {
                        DateTime modifiedTime = ((action.Argument != 0) ? SshSession.UnixEpoch.AddSeconds(action.Argument) : DateTime.UtcNow.AddSeconds(-5.0));
                        destinationStream.Write(tarWriter.CreateHeader(relativeRemotePath.Combine(action.Path).Path, isDirectory: true, 0L, modifiedTime.AddSeconds(_settings.ClockOffset.Seconds), out paddingBytes), 0, 512);
                        goto case SyncPlan.ActionType.EnterDirectory;
                    }
                case SyncPlan.ActionType.EnterDirectory:
                    remoteDir = RemotePathUtils.Combine(remoteDir, action.Path);
                    relativeRemotePath = relativeRemotePath.Combine(action.Path);
                    streamWriter.WriteLine($"cd \"{remoteDir}\" || echo \"cmdfail:{i}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    remoteDir = RemotePathUtils.GetDirectoryName(remoteDir);
                    relativeRemotePath = new RelativeRemotePath(RemotePathUtils.GetDirectoryName(relativeRemotePath.Path));
                    streamWriter.WriteLine("cd \"" + remoteDir + "\"");
                    break;
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
                        scriptCommandCount++;
                        break;
                    }
                case SyncPlan.ActionType.TransferFile:
                    {
                        QuickSyncWarningType warningType = QuickSyncWarningType.CannotReadLocalFile;
                        bool headerWritten = false;
                        try
                        {
                            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(localPaths[action.Argument], LocalFileSystem.FileOpenMode.Open);
                            long writeTime = nativeFile.GetLastWriteTime();
                            writeTime += (long)_settings.ClockOffset.Seconds * 10000000L;
                            long fileSize = nativeFile.GetSize();
                            warningType = QuickSyncWarningType.CannotCreateTargetFile;
                            int fileIndex = action.Argument;
                            int permissions = permissionProvider?.GetFilePermissions(localPaths[fileIndex]) ?? defaultPermissions;
                            fileStamps[fileIndex] = new LocalFileSystem.FileStamp(fileSize, writeTime);
                            byte[] header = tarWriter.CreateHeader(relativeRemotePath.Combine(action.Path).Path, isDirectory: false, fileSize, DateTime.FromFileTimeUtc(writeTime), out paddingBytes, isLongLink: false, permissions);
                            destinationStream.Write(header, 0, header.Length);
                            headerWritten = true;
                            bool shortReadOccurred = false;
                            long transferred = 0L;
                            while (transferred < fileSize)
                            {
                                int bytesToRead = (int)Math.Min(fileSize - transferred, copyBuffer.Length);
                                int bytesRead;
                                try
                                {
                                    bytesRead = nativeFile.Read(copyBuffer, bytesToRead);
                                }
                                catch
                                {
                                    bytesRead = 0;
                                }
                                if (bytesRead < bytesToRead)
                                {
                                    shortReadOccurred = true;
                                    while (bytesRead < bytesToRead)
                                    {
                                        copyBuffer[bytesRead++] = 0;
                                    }
                                }
                                destinationStream.Write(copyBuffer, 0, bytesRead);
                                transferred += bytesRead;
                                transferStatistics.TransferredBytes += bytesRead;
                                base._events?.OnProgress(new SyncProgress
                                {
                                    Stage = SyncStage.Transferring,
                                    CurrentFile = localPaths[action.Argument].FullPath,
                                    Transferred = transferStatistics.TransferredBytes,
                                    Total = transferStatistics.TotalBytes
                                });
                            }
                            transferStatistics.CopiedFiles++;
                            if (paddingBytes > paddingBuffer.Length)
                            {
                                paddingBuffer = new byte[paddingBytes];
                            }
                            destinationStream.Write(paddingBuffer, 0, paddingBytes);
                            if (shortReadOccurred)
                            {
                                base._events.OnWarning(new SyncWarning
                                {
                                    Warning = new QuickSyncWarning(QuickSyncWarningType.CannotReadLocalFile, localPaths[action.Argument].FullPath, 0)
                                });
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (headerWritten)
                            {
                                throw;
                            }
                            base._events.OnWarning(new SyncWarning
                            {
                                Exception = ex,
                                Warning = new QuickSyncWarning(warningType, localPaths[action.Argument].FullPath, 0)
                            });
                        }
                        break;
                    }
                case SyncPlan.ActionType.DeleteTargetFile:
                    streamWriter.WriteLine($"rm -f \"{action.Path}\" || echo \"cmdfail:{i}\"");
                    scriptCommandCount++;
                    break;
            }
        }
        streamWriter.Flush();
        if (scriptCommandCount == 0)
        {
            return default;
        }
        return new TarPayload(marker, memoryStream.ToArray());
    }
}
