using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
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

        private void OnFinished(object obj)
        {
        }

        internal void OnDataReceived(object obj, byte[] buffer, int value2, int value3, int value4)
        {
            try
            {
                if (value4 != 1)
                {
                    return;
                }
                foreach (string item in this._lineSplitter.Feed(buffer, value2, value3))
                {
                    HasOutput = true;
                    StderrLine?.Invoke(item);
                }
            }
            catch
            {
            }
        }
    }

    private struct TarPayload(string text, byte[] buffer)
    {
        public readonly string Path = text;

        public readonly byte[] Data = buffer;
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

    public override void Upload(List<SyncPlan.SyncAction> list2, List<LocalPath> list3, ref TransferStatistics transferStatistics)
    {
        string text = "LANG=en_US " + DirectoryCopier.TarCommands.GetExtractCommand(_settings.RemotePath, _settings.ClockOffset.IsKnown);
        IShellLauncher shellLauncher = null;
        if ((_settings.Options & SyncOptions.UseLauncher) != 0)
        {
            shellLauncher = _customization?.GetLauncher(text);
        }
        LocalFileSystem.FileStamp[] array = new LocalFileSystem.FileStamp[list3.Count];
        if (_session.RunCommandChecked("mkdir -p \"" + _settings.RemotePath + "\"", flag: true) == null)
        {
            throw new Exception("Failed to create " + _settings.RemotePath);
        }
        TarPayload tarPayload;
        using (SshShellChannel sshShellChannel = _session.OpenShellChannel(shellLauncher?.Command ?? text))
        {
            shellLauncher?.Prepare(sshShellChannel, "Outgoing file transfers");
            StderrMonitor stderrMonitor = new StderrMonitor(this, sshShellChannel);
            sshShellChannel.PollingEnabled = true;
            stderrMonitor.StderrLine += (string text2) =>
            {
                base._events?.OnWarning(new SyncWarning
                {
                    Warning = new QuickSyncWarning(QuickSyncWarningType.StderrLine, text2, 0)
                });
            };
            tarPayload = this.WriteArchive(new TarWriter(null), sshShellChannel.Input, list2, list3, ref transferStatistics, array);
            sshShellChannel.Input.Write(new byte[1024], 0, 1024);
            sshShellChannel.SendEof();
            sshShellChannel.WaitEof();
            if (!stderrMonitor.HasOutput && sshShellChannel.ExitStatus == 0 && _cache != null)
            {
                for (int num = 0; num < list3.Count; num++)
                {
                    if (array[num].HasTimestamp)
                    {
                        _cache.StoreFileStamp(num, array[num]);
                    }
                }
            }
        }
        if (tarPayload.Data == null)
        {
            return;
        }
        List<string> list = RunScript(tarPayload.Path, tarPayload.Data);
        bool flag = false;
        foreach (string item in list)
        {
            int result;
            if (!flag)
            {
                if (!(item != tarPayload.Path))
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

    private TarPayload WriteArchive(TarWriter tarWriter, Stream stream, List<SyncPlan.SyncAction> list, List<LocalPath> list2, ref TransferStatistics transferStatistics, LocalFileSystem.FileStamp[] items)
    {
        string text = Guid.NewGuid().ToString();
        byte[] array = new byte[Tuning.BufferSize];
        MemoryStream memoryStream = new MemoryStream();
        string text2 = _settings.RemotePath;
        RelativeRemotePath relativeRemotePath = default;
        byte[] array2 = new byte[1024];
        IPermissionProvider permissionProvider = base._events?.PermissionProvider;
        int num = Convert.ToInt32("755", 8);
        int num2 = 0;
        using StreamWriter streamWriter = new StreamWriter(memoryStream)
        {
            NewLine = "\n"
        };
        streamWriter.WriteLine("echo " + text);
        streamWriter.WriteLine("cd \"" + _settings.RemotePath + "\" || exit 1");
        for (int num3 = 0; num3 < list.Count; num3++)
        {
            SyncPlan.SyncAction syncAction = list[num3];
            int num7;
            switch (syncAction.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                    {
                        DateTime dateTime = ((syncAction.Argument != 0) ? LibSsh2Session.UnixEpoch.AddSeconds(syncAction.Argument) : DateTime.UtcNow.AddSeconds(-5.0));
                        stream.Write(tarWriter.CreateHeader(relativeRemotePath.Combine(syncAction.Path).Path, flag: true, 0L, dateTime.AddSeconds(_settings.ClockOffset.Seconds), out num7), 0, 512);
                        goto case SyncPlan.ActionType.EnterDirectory;
                    }
                case SyncPlan.ActionType.EnterDirectory:
                    text2 = RemotePathUtils.Combine(text2, syncAction.Path);
                    relativeRemotePath = relativeRemotePath.Combine(syncAction.Path);
                    streamWriter.WriteLine($"cd \"{text2}\" || echo \"cmdfail:{num3}\"");
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    text2 = RemotePathUtils.GetDirectoryName(text2);
                    relativeRemotePath = new RelativeRemotePath(RemotePathUtils.GetDirectoryName(relativeRemotePath.Path));
                    streamWriter.WriteLine("cd \"" + text2 + "\"");
                    break;
                case SyncPlan.ActionType.CreateRemoteSymlink:
                    {
                        KeyValuePair<string, string> keyValuePair = StringUtils.SplitAtFirst(syncAction.Path, '\n');
                        string text3 = RemotePathUtils.GetFileName(keyValuePair.Key);
                        if (keyValuePair.Key == null || keyValuePair.Value == null)
                        {
                            throw new Exception("Invalid queued symlink: " + syncAction.Path);
                        }
                        streamWriter.WriteLine("rm -f \"" + text3 + "\"");
                        streamWriter.WriteLine($"ln -s \"{keyValuePair.Value}\" \"{text3}\" || echo \"cmdfail:{num3}\"");
                        num2++;
                        break;
                    }
                case SyncPlan.ActionType.TransferFile:
                    {
                        QuickSyncWarningType quickSyncWarningType = QuickSyncWarningType.CannotReadLocalFile;
                        bool flag = false;
                        try
                        {
                            using LocalFileSystem.NativeFile nativeFile = LocalFileSystem.OpenFile(list2[syncAction.Argument], LocalFileSystem.FileOpenMode.Open);
                            long num4 = nativeFile.GetLastWriteTime();
                            num4 += (long)_settings.ClockOffset.Seconds * 10000000L;
                            long num5 = nativeFile.GetSize();
                            quickSyncWarningType = QuickSyncWarningType.CannotCreateTargetFile;
                            int num6 = syncAction.Argument;
                            int value = permissionProvider?.GetFilePermissions(list2[num6]) ?? num;
                            items[num6] = new LocalFileSystem.FileStamp(num5, num4);
                            byte[] array3 = tarWriter.CreateHeader(relativeRemotePath.Combine(syncAction.Path).Path, flag: false, num5, DateTime.FromFileTimeUtc(num4), out num7, flag2: false, value);
                            stream.Write(array3, 0, array3.Length);
                            flag = true;
                            bool flag2 = false;
                            long num8 = 0L;
                            while (num8 < num5)
                            {
                                int num9 = (int)Math.Min(num5 - num8, array.Length);
                                int num10;
                                try
                                {
                                    num10 = nativeFile.Read(array, num9);
                                }
                                catch
                                {
                                    num10 = 0;
                                }
                                if (num10 < num9)
                                {
                                    flag2 = true;
                                    while (num10 < num9)
                                    {
                                        array[num10++] = 0;
                                    }
                                }
                                stream.Write(array, 0, num10);
                                num8 += num10;
                                transferStatistics.TransferredBytes += num10;
                                base._events?.OnProgress(new SyncProgress
                                {
                                    Stage = SyncStage.Transferring,
                                    CurrentFile = list2[syncAction.Argument].FullPath,
                                    Transferred = transferStatistics.TransferredBytes,
                                    Total = transferStatistics.TotalBytes
                                });
                            }
                            transferStatistics.CopiedFiles++;
                            if (num7 > array2.Length)
                            {
                                array2 = new byte[num7];
                            }
                            stream.Write(array2, 0, num7);
                            if (flag2)
                            {
                                base._events.OnWarning(new SyncWarning
                                {
                                    Warning = new QuickSyncWarning(QuickSyncWarningType.CannotReadLocalFile, list2[syncAction.Argument].FullPath, 0)
                                });
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex2)
                        {
                            if (flag)
                            {
                                throw;
                            }
                            base._events.OnWarning(new SyncWarning
                            {
                                Exception = ex2,
                                Warning = new QuickSyncWarning(quickSyncWarningType, list2[syncAction.Argument].FullPath, 0)
                            });
                        }
                        break;
                    }
                case SyncPlan.ActionType.DeleteTargetFile:
                    streamWriter.WriteLine($"rm -f \"{syncAction.Path}\" || echo \"cmdfail:{num3}\"");
                    num2++;
                    break;
            }
        }
        streamWriter.Flush();
        if (num2 == 0)
        {
            return default;
        }
        return new TarPayload(text, memoryStream.ToArray());
    }
}
