using ManagedLibSSH2;
using ManagedLibSSH2.QuickSync;
using SmarTTY.IO;
using SmarTTY.Native;
using SmarTTY.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmarTTY.Sync;

public class QuickSyncRunner
{
    private class LoggingSyncEvents : ISyncDiagnostics, ISyncEvents
    {
        private ISyncEvents _inner;

        private StreamWriter _writer;

        private StringBuilder _warnings = new StringBuilder();

        public IPermissionProvider PermissionProvider => _inner?.PermissionProvider;

        public LoggingSyncEvents(ISyncEvents syncEvents, StreamWriter streamWriter)
        {
            _inner = syncEvents;
            _writer = streamWriter;
        }

        public void OnLog(SyncLogMessage syncLogMessage)
        {
            _inner?.OnLog(syncLogMessage);
        }

        public void OnPlanCreated(SyncPlan syncPlan)
        {
            (_inner as ISyncDiagnostics)?.OnPlanCreated(syncPlan);
            _writer.WriteLine("Regular action list:");
            foreach (SyncPlan.SyncAction item in syncPlan.Actions)
            {
                _writer.WriteLine($"  {item.Type} {item.Path} {item.Argument}");
            }
            _writer.WriteLine("--------------------------------------");
            _writer.WriteLine();
            _writer.WriteLine("Symlink action list:");
            foreach (SyncPlan.SymlinkRequest item2 in syncPlan.Symlinks)
            {
                _writer.WriteLine("  " + item2.LinkPath + " => " + item2.Target + (item2.IsDirectory ? "(dir)" : "(file)"));
            }
            _writer.WriteLine("--------------------------------------");
            _writer.WriteLine();
        }

        public void OnProgress(SyncProgress syncProgress)
        {
            _inner?.OnProgress(syncProgress);
        }

        public void OnRemoteFileSet(ISyncEngine syncEngine, RemoteFileSet remoteFileSet)
        {
            (_inner as ISyncDiagnostics)?.OnRemoteFileSet(syncEngine, remoteFileSet);
            _writer.WriteLine($"Using {syncEngine}");
            _writer.WriteLine("Received remote file set:");
            remoteFileSet.Root.Dump(_writer);
            _writer.WriteLine("--------------------------------------");
            _writer.WriteLine();
        }

        public void OnScanFinished()
        {
            (_inner as ISyncDiagnostics)?.OnScanFinished();
        }

        public void OnWarning(SyncWarning syncWarning)
        {
            _inner?.OnWarning(syncWarning);
            _warnings.AppendLine(syncWarning.ToString());
        }

        internal void WriteWarnings()
        {
            _writer.WriteLine("Warnings:");
            _writer.WriteLine(_warnings);
            _writer.WriteLine("--------------------------------------");
            _writer.WriteLine();
        }

        internal void WriteLine(string text)
        {
            _writer.WriteLine(text);
        }
    }

    private class FileSetCaptureEvents : ISyncDiagnostics, ISyncEvents
    {
        private ISyncEvents _inner;

        public RemoteFileSet CapturedFileSet { get; private set; }

        public IPermissionProvider PermissionProvider => _inner?.PermissionProvider;

        public FileSetCaptureEvents(ISyncEvents syncEvents)
        {
            _inner = syncEvents;
        }

        public void OnLog(SyncLogMessage syncLogMessage)
        {
            _inner?.OnLog(syncLogMessage);
        }

        public void OnPlanCreated(SyncPlan syncPlan)
        {
        }

        public void OnProgress(SyncProgress syncProgress)
        {
            _inner?.OnProgress(syncProgress);
        }

        public void OnRemoteFileSet(ISyncEngine syncEngine, RemoteFileSet remoteFileSet)
        {
            CapturedFileSet = remoteFileSet;
            throw new OperationCanceledException();
        }

        public void OnScanFinished()
        {
        }

        public void OnWarning(SyncWarning syncWarning)
        {
            _inner?.OnWarning(syncWarning);
        }
    }

    public enum SyncEngineKind
    {
        Auto,
        SysprogsSync,
        FileByFile,
        Tar
    }

    private class HelperBuilder : IHelperInstaller
    {
        private enum BuildFlags
        {
            Default,
            UseRegexHeader,
            LargeFileOffset
        }

        private readonly SshSession _session;

        private readonly string _sourcePath;

        private string _outputPath;

        public string HostName => _session.HostName;

        public HelperBuilder(SshSession sshSession, string text, string text2)
        {
            _session = sshSession;
            this._sourcePath = text;
            this._outputPath = text2;
        }

        public void Install(Action<string> action)
        {
            string text = _session.GetTempPath("SysprogsSync.cpp");
            action?.Invoke("Sending " + this._sourcePath + " to " + text + "...");
            byte[] array = File.ReadAllBytes(this._sourcePath);
            SshSession sshSession = _session;
            long num = array.Length;
            int? num2 = Convert.ToInt32("666", 8);
            using (ScpChannelStream scpChannelStream = sshSession.OpenUpload(text, num, default, num2))
            {
                scpChannelStream.Write(array, 0, array.Length);
            }
            BuildFlags buildFlags = BuildFlags.Default;
            BuildFlags buildFlags2;
            do
            {
                string text2 = "g++ " + text + " -O3 -o " + _outputPath;
                text2 = (((buildFlags & BuildFlags.UseRegexHeader) == 0) ? (text2 + " -std=c++11") : (text2 + " -DUSE_REGEX_H"));
                if ((buildFlags & BuildFlags.LargeFileOffset) != 0)
                {
                    text2 += " -D_FILE_OFFSET_BITS=64";
                }
                List<string> coll = _session.RunCommand(text2, out var num3);
                buildFlags2 = buildFlags;
                foreach (string item in coll.EnsureNotNull())
                {
                    action?.Invoke(item);
                    if (item.Contains("stat64"))
                    {
                        buildFlags |= BuildFlags.LargeFileOffset;
                    }
                    if (item.Contains("regex") || item.Contains("c++11"))
                    {
                        buildFlags |= BuildFlags.UseRegexHeader;
                    }
                }
                if (num3 != 0)
                {
                    continue;
                }
                List<string> coll2 = _session.RunCommand(_outputPath + " --selftest", out num3);
                if (num3 == 0)
                {
                    return;
                }
                foreach (string item2 in coll2.EnsureNotNull())
                {
                    action?.Invoke(item2);
                }
                buildFlags |= BuildFlags.UseRegexHeader;
            }
            while (buildFlags != buildFlags2);
            throw new Exception("Failed to build remote SysprogsSync executable. Please try building it manually.");
        }
    }

    private struct LocalDirectoryContext(LocalPath localPath)
    {
        public LocalPath Path = localPath;

        public bool Failed = false;

        public LocalDirectoryContext GetChild(string text)
        {
            LocalDirectoryContext result = new LocalDirectoryContext(Path.Combine(text));
            result.Failed = this.Failed;
            return result;
        }
    }

    private readonly string _helperSourcePath;

    private readonly IHelperLocator _helperLocator;

    internal SyncEngineKind _engineKind;

    public SyncTuning Tuning = SyncTuning.Default;

    public QuickSyncRunner(string text, IHelperLocator helperLocator = null)
    {
        _helperSourcePath = text;
        _helperLocator = helperLocator;
    }

    public TransferStatistics Run(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache = null, ISyncCustomization syncCustomization = null, StreamWriter streamWriter = null)
    {
        if (streamWriter != null)
        {
            syncEvents = new LoggingSyncEvents(syncEvents, streamWriter);
        }
        TransferStatistics result;
        try
        {
            syncSettings.LocalPath = ((syncSettings.LocalPath == null) ? null : PathUtils.GetFullPathChecked(syncSettings.LocalPath));
            syncSettings.RemotePath = RemotePathUtils.NormalizeUnix(syncSettings.RemotePath);
            using ISyncEngine syncEngine = this.CreateEngine(sshSession, syncSettings, syncEvents, localFileCache, syncCustomization);
            syncEvents?.OnProgress(new SyncProgress
            {
                Stage = SyncStage.BuildingFileList
            });
            (syncEvents as ISyncDiagnostics)?.OnScanFinished();
            RemoteFileSet remoteFileSet = ((syncSettings.Direction != SyncDirection.Upload || syncSettings.QuickSyncOverwriteTrigger != QuickSyncOverwriteTrigger.LocalCache || localFileCache == null) ? syncEngine.GetRemoteFileSet() : new RemoteFileSet
            {
                Root = new RemoteFileSet.RemoteDirectory(null, null)
            });
            (syncEvents as ISyncDiagnostics)?.OnRemoteFileSet(syncEngine, remoteFileSet);
            if (syncSettings.Direction == SyncDirection.Download)
            {
                LocalFileSystem.CreateDirectoryRecursive(LocalPath.FromUserPath(syncSettings.LocalPath));
                SyncPlan.DownloadPlanner downloadPlanner = new SyncPlan.DownloadPlanner(syncSettings, remoteFileSet);
                ((SyncPlan.SyncPlannerBase)downloadPlanner).Warning += (QuickSyncWarning quickSyncWarning, Exception exception) =>
                {
                    syncEvents?.OnWarning(new SyncWarning
                    {
                        Warning = quickSyncWarning,
                        Exception = exception
                    });
                };
                SyncPlan syncPlan = downloadPlanner.BuildPlan();
                result = downloadPlanner.Statistics;
                (syncEvents as ISyncDiagnostics)?.OnPlanCreated(syncPlan);
                LocalFileSystem.LinkOptions linkOptions = LocalFileSystem.LinkOptions.None;
                if ((syncSettings.Options & SyncOptions.SkipSymlinks) == SyncOptions.SkipSymlinks)
                {
                    linkOptions |= LocalFileSystem.LinkOptions.SkipSymlinks;
                }
                if ((syncSettings.Options & SyncOptions.SkipHardlinks) == SyncOptions.SkipHardlinks)
                {
                    linkOptions |= LocalFileSystem.LinkOptions.SkipHardlinks;
                }
                if ((syncSettings.Options & SyncOptions.DeleteExtraneous) == SyncOptions.DeleteExtraneous)
                {
                    linkOptions |= LocalFileSystem.LinkOptions.DeleteExtraneous;
                }
                using IRemoteFileSource remoteFileSource = syncEngine.BeginDownload(syncPlan.Actions);
                this.ExecuteLocalActions(LocalPath.FromUserPath(syncSettings.LocalPath), syncPlan, remoteFileSource, syncEvents, ref result);
                CreateLinks(syncPlan, linkOptions, syncEvents);
            }
            else
            {
                if (syncSettings.Direction != SyncDirection.Upload)
                {
                    throw new ArgumentException("Direction");
                }
                SyncPlan.UploadPlanner uploadPlanner = new SyncPlan.UploadPlanner(flag: remoteFileSet.Root.Files.Count <= 0 && remoteFileSet.Root.SubDirectories.Count <= 0 && remoteFileSet.Root.Symlinks.Count <= 0 && sshSession.RunCommandChecked("test -d \"" + syncSettings.RemotePath + "\"", flag: true) == null, syncSettings: syncSettings, remoteFileSet: remoteFileSet, localFileCache: localFileCache, syncCustomization: syncCustomization);
                uploadPlanner.Warning += (QuickSyncWarning quickSyncWarning, Exception exception) =>
                {
                    syncEvents?.OnWarning(new SyncWarning
                    {
                        Warning = quickSyncWarning,
                        Exception = exception
                    });
                };
                SyncPlan syncPlan2 = uploadPlanner.BuildPlan();
                result = uploadPlanner.Statistics;
                (syncEvents as ISyncDiagnostics)?.OnPlanCreated(syncPlan2);
                syncEngine.Upload(syncPlan2.Actions, uploadPlanner.Files, ref result);
            }
        }
        finally
        {
            (syncEvents as LoggingSyncEvents)?.WriteWarnings();
        }
        return result;
    }

    private void ScanLocalDirectory(RemoteFileSet.RemoteDirectory remoteDirectory, LocalPath localPath, ISyncEvents syncEvents)
    {
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.BuildingFileList
        });
        foreach (LocalFileSystem.FindData item2 in LocalFileSystem.EnumerateDirectory(localPath, flag: false))
        {
            if (item2.HasAttribute(FileAttributes.ReparsePoint))
            {
                remoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                {
                    Name = item2.FileName,
                    IsDirectory = item2.HasAttribute(FileAttributes.Directory)
                });
            }
            else if (item2.HasAttribute(FileAttributes.Directory))
            {
                RemoteFileSet.RemoteDirectory item = new RemoteFileSet.RemoteDirectory(remoteDirectory, item2.FileName);
                try
                {
                    ScanLocalDirectory(item, localPath.Combine(item2.FileName), syncEvents);
                    remoteDirectory.SubDirectories.Add(item);
                }
                catch (Exception ex)
                {
                    syncEvents?.OnWarning(new SyncWarning
                    {
                        Exception = ex,
                        Warning = new QuickSyncWarning(QuickSyncWarningType.UnreadableLocalDirectory, localPath.ToString(), 0)
                    });
                }
            }
            else
            {
                LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(item2);
                remoteDirectory.Files.Add(new RemoteFileSet.RemoteFileEntry
                {
                    Name = item2.FileName,
                    Size = fileStamp.Size,
                    ModifiedSeconds = fileStamp.UnixSeconds
                });
            }
        }
    }

    public RemoteFileSet GetRemoteFileSet(SshSession sshSession, string text, ISyncEvents syncEvents, SyncOptions syncOptions = SyncOptions.None, QuickSyncFilter[] items = null)
    {
        if (sshSession == null)
        {
            text = PathUtils.GetFullPathChecked(text);
            RemoteFileSet.RemoteDirectory remoteDirectory = new RemoteFileSet.RemoteDirectory(null, null);
            ScanLocalDirectory(remoteDirectory, LocalPath.FromUserPath(text), syncEvents);
            return new RemoteFileSet
            {
                Root = remoteDirectory
            };
        }
        FileSetCaptureEvents fileSetCaptureEvents = new FileSetCaptureEvents(syncEvents);
        text = RemotePathUtils.NormalizeUnix(text);
        try
        {
            Run(sshSession, new SyncSettings
            {
                Direction = SyncDirection.Download,
                RemotePath = text,
                Options = syncOptions,
                FileFilters = items
            }, fileSetCaptureEvents);
            throw new Exception("Failed to obtain remote file set");
        }
        catch (OperationCanceledException)
        {
            return fileSetCaptureEvents.CapturedFileSet ?? throw new OperationCanceledException();
        }
    }

    public static bool IsIncluded(string text, QuickSyncFilter[] items)
    {
        if (string.IsNullOrEmpty(text) || items == null)
        {
            return true;
        }
        for (int num = 0; num < items.Length; num++)
        {
            QuickSyncFilter quickSyncFilter = items[num];
            if (quickSyncFilter.IsMatch(text))
            {
                return quickSyncFilter.Action == QuickSyncAction.Include;
            }
        }
        return false;
    }

    public void DeleteLocalItems(string text, string[] items, ISyncEvents syncEvents)
    {
        SyncPlan.DownloadPlanner downloadPlanner = new SyncPlan.DownloadPlanner(new SyncSettings
        {
            LocalPath = text,
            RemotePath = "/"
        }, new RemoteFileSet());
        LocalPath localPath = LocalPath.FromUserPath(text);
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.BuildingFileList,
            Total = -1L
        });
        SyncPlan syncPlan = downloadPlanner.PlanDeleteNames(localPath, items);
        TransferStatistics transferStatistics = downloadPlanner.Statistics;
        this.ExecuteLocalActions(localPath, syncPlan, null, syncEvents, ref transferStatistics, flag2: true);
    }

    private ISyncEngine CreateEngine(SshSession sshSession, SyncSettings syncSettings, ISyncEvents syncEvents, ILocalFileCache localFileCache, ISyncCustomization syncCustomization)
    {
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.Initializing
        });
        if (_engineKind == SyncEngineKind.FileByFile)
        {
            return new FileByFileSyncEngine(sshSession, syncSettings, syncEvents, localFileCache, Tuning, syncCustomization);
        }
        bool flag = (syncSettings.Options & SyncOptions.DisableHelper) == SyncOptions.DisableHelper;
        bool flag2 = false;
        if (_engineKind == SyncEngineKind.Tar)
        {
            flag2 = true;
        }
        else if (_engineKind == SyncEngineKind.Auto && (syncSettings.Options & SyncOptions.DisableTar) != SyncOptions.DisableTar && syncSettings.Direction == SyncDirection.Upload && sshSession.EnableOnTheFlyTar && ((syncSettings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache && localFileCache != null) || syncSettings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.Always))
        {
            flag2 = true;
        }
        if (flag2)
        {
            return new TarSyncEngine(sshSession, syncSettings, syncEvents, localFileCache, Tuning, syncCustomization);
        }
        if (sshSession.Tag == null && !flag)
        {
            string text = this._helperLocator?.FindHelper(sshSession.HostName);
            if (text != null && sshSession.RunCommandChecked("test -f \"" + text + "\"", flag: true) != null)
            {
                sshSession.Tag = text;
            }
            else
            {
                string text2 = "SysprogsSync-v1.2";
                if (sshSession.RunCommandChecked("which -f \"" + text2 + "\"", flag: true) != null || sshSession.RunCommandChecked("which \"" + text2 + "\"", flag: true) != null)
                {
                    sshSession.Tag = text2;
                }
                else
                {
                    string text3 = sshSession.RunCommandChecked("pwd", flag: true)?.FirstOrDefault((string text5) => text5.StartsWith("/"));
                    if (text3 == null)
                    {
                        text3 = RemotePathUtils.GetDirectoryName(sshSession.GetTempPath("test"));
                    }
                    text = text3 + "/.SysprogsSync/" + text2;
                    if (text != null && sshSession.RunCommandChecked("test -f \"" + text + "\"", flag: true) != null)
                    {
                        sshSession.Tag = text;
                    }
                    else
                    {
                        string text4 = text3 + "/.SysprogsSync";
                        sshSession.RunCommandChecked("mkdir -p \"" + text4 + "\"", flag: false);
                        text = text4 + "/SysprogsSync-v1.2";
                        HelperBuilder helperBuilder = new HelperBuilder(sshSession, this._helperSourcePath, text);
                        try
                        {
                            if (this._helperLocator == null)
                            {
                                helperBuilder.Install(null);
                            }
                            else
                            {
                                this._helperLocator.InstallHelper(helperBuilder);
                            }
                            sshSession.Tag = text;
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        if ((sshSession.Tag == null) | flag)
        {
            if (_engineKind == SyncEngineKind.SysprogsSync)
            {
                throw new Exception("Could not install SysprogsSync");
            }
            syncEvents?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(QuickSyncWarningType.UsingSlowEngine, "file-by-file", 0)
            });
            return new FileByFileSyncEngine(sshSession, syncSettings, syncEvents, localFileCache, Tuning, syncCustomization);
        }
        return new SysprogsSyncEngine(sshSession, syncSettings, syncEvents, localFileCache, Tuning, sshSession.Tag, syncCustomization);
    }

    private void CreateLinks(SyncPlan syncPlan, LocalFileSystem.LinkOptions linkOptions, ISyncEvents syncEvents)
    {
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.UpdatingSymlinks
        });
        Action<RemoteWarningRecord> action = (RemoteWarningRecord remoteWarningRecord) =>
        {
            syncEvents?.OnWarning(new SyncWarning
            {
                Warning = new QuickSyncWarning(remoteWarningRecord)
            });
        };
        foreach (SyncPlan.SymlinkRequest item in syncPlan.Symlinks)
        {
            try
            {
                if (!item.IsDirectory)
                {
                    LocalFileSystem.CreateFileLink(LocalPath.FromRaw(item.LinkPath), item.Target, linkOptions, action);
                }
                else
                {
                    LocalFileSystem.CreateDirectoryLink(LocalPath.FromRaw(item.LinkPath), item.Target, linkOptions, action);
                }
            }
            catch (Exception ex)
            {
                syncEvents?.OnWarning(new SyncWarning
                {
                    Exception = ex,
                    Warning = new QuickSyncWarning(QuickSyncWarningType.CreateSymlinkFailed, item.LinkPath, 0)
                });
            }
        }
    }

    private static string GetRelativePath(LocalPath localPath, LocalPath localPath2)
    {
        if (localPath2.FullPath.StartsWith(localPath.FullPath, StringComparison.InvariantCultureIgnoreCase))
        {
            return localPath2.FullPath.Substring(localPath.FullPath.Length).TrimStart('\\').Replace('\\', '/');
        }
        return localPath2.FullPath;
    }

    private void ExecuteLocalActions(LocalPath localPath, SyncPlan syncPlan, IRemoteFileSource remoteFileSource, ISyncEvents syncEvents, ref TransferStatistics transferStatistics, bool flag2 = false)
    {
        Stack<LocalDirectoryContext> stack = new Stack<LocalDirectoryContext>();
        LocalDirectoryContext item = new LocalDirectoryContext(localPath);
        LocalFileSystem.CreateDirectoryRecursive(localPath);
        byte[] array = null;
        foreach (SyncPlan.SyncAction item2 in syncPlan.Actions)
        {
            switch (item2.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                case SyncPlan.ActionType.EnterDirectory:
                    stack.Push(item);
                    item = item.GetChild(item2.Path);
                    if (!item.Failed && item2.Type == SyncPlan.ActionType.CreateAndEnterDirectory)
                    {
                        try
                        {
                            LocalFileSystem.CreateDirectoryRecursive(item.Path);
                        }
                        catch (Exception ex5)
                        {
                            item.Failed = true;
                            syncEvents?.OnWarning(new SyncWarning
                            {
                                Exception = ex5,
                                Warning = new QuickSyncWarning
                                {
                                    Type = QuickSyncWarningType.CannotCreateTargetDirectory,
                                    Path = item.Path.FullPath
                                }
                            });
                        }
                    }
                    break;
                case SyncPlan.ActionType.GoToParentDirectory:
                    if (stack.Count == 0)
                    {
                        throw new Exception("Unbalanced directory stack detected");
                    }
                    item = stack.Pop();
                    break;
                case SyncPlan.ActionType.TransferFile:
                    {
                        LocalFileSystem.NativeFile nativeFile = null;
                        string text = GetRelativePath(localPath, item.Path.Combine(item2.Path));
                        if (!item.Failed)
                        {
                            try
                            {
                                nativeFile = LocalFileSystem.OpenFile(item.Path.Combine(item2.Path), LocalFileSystem.FileOpenMode.Create);
                            }
                            catch (Exception ex3)
                            {
                                syncEvents?.OnWarning(new SyncWarning
                                {
                                    Exception = ex3,
                                    Warning = new QuickSyncWarning
                                    {
                                        Type = QuickSyncWarningType.CannotCreateTargetFile,
                                        Path = text
                                    }
                                });
                            }
                        }
                        bool flag = false;
                        using (IRemoteFileStream remoteFileStream = remoteFileSource.OpenFile(item2.Argument))
                        {
                            using (nativeFile)
                            {
                                if (remoteFileStream.ErrorCode != 0)
                                {
                                    syncEvents?.OnWarning(new SyncWarning
                                    {
                                        Warning = new QuickSyncWarning
                                        {
                                            Type = QuickSyncWarningType.CannotReadRemoteFile,
                                            Path = text,
                                            ErrorCode = remoteFileStream.ErrorCode
                                        }
                                    });
                                }
                                while (true)
                                {
                                    int num = remoteFileStream.ReadChunk(ref array);
                                    if (num == 0)
                                    {
                                        break;
                                    }
                                    transferStatistics.TransferredBytes += num;
                                    syncEvents?.OnProgress(new SyncProgress
                                    {
                                        Stage = SyncStage.Transferring,
                                        CurrentFile = text,
                                        Transferred = transferStatistics.TransferredBytes,
                                        Total = transferStatistics.TotalBytes
                                    });
                                    if (nativeFile == null || flag)
                                    {
                                        continue;
                                    }
                                    try
                                    {
                                        if (nativeFile.Write(array, num) != num)
                                        {
                                            throw new IOException("Could not write all bytes to the file");
                                        }
                                    }
                                    catch (Exception ex4)
                                    {
                                        flag = true;
                                        syncEvents?.OnWarning(new SyncWarning
                                        {
                                            Exception = ex4,
                                            Warning = new QuickSyncWarning
                                            {
                                                Type = QuickSyncWarningType.CannotUpdateLocalFile,
                                                Path = item.Path.Combine(item2.Path).FullPath
                                            }
                                        });
                                    }
                                }
                                if (remoteFileStream.ErrorCode == 0 && !flag && nativeFile != null)
                                {
                                    nativeFile.SetLastWriteTime(remoteFileStream.ModifiedTime);
                                    transferStatistics.CopiedFiles++;
                                }
                            }
                        }
                        break;
                    }
                case SyncPlan.ActionType.DeleteTargetFile:
                    try
                    {
                        LocalFileSystem.DeleteFile(LocalPath.FromRaw(item2.Path), flag: true);
                        transferStatistics.DeletedFiles++;
                        if (flag2)
                        {
                            syncEvents?.OnProgress(new SyncProgress
                            {
                                Stage = SyncStage.DeletingOldContents,
                                Transferred = transferStatistics.DeletedFiles,
                                Total = transferStatistics.FilesToDelete
                            });
                        }
                    }
                    catch (Exception ex2)
                    {
                        syncEvents?.OnWarning(new SyncWarning
                        {
                            Exception = ex2,
                            Warning = new QuickSyncWarning
                            {
                                Type = QuickSyncWarningType.CannotDeleteTargetFile,
                                Path = item.Path.Combine(item2.Path).FullPath
                            }
                        });
                    }
                    break;
                case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                    try
                    {
                        LocalFileSystem.DeleteDirectory(LocalPath.FromRaw(item2.Path));
                    }
                    catch (Exception ex)
                    {
                        syncEvents?.OnWarning(new SyncWarning
                        {
                            Exception = ex,
                            Warning = new QuickSyncWarning
                            {
                                Type = QuickSyncWarningType.CannotRemoveTargetDirectory,
                                Path = item.Path.Combine(item2.Path).FullPath
                            }
                        });
                    }
                    break;
            }
        }
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.Transferring
        });
    }

    public static void CacheLocalFiles(ILocalFileCache localFileCache, LocalPath localPath, bool flag)
    {
        ICachedDirectory cachedDirectory = localFileCache.GetDirectory(localPath);
        foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath, flag: true))
        {
            if (item.HasAttribute(FileAttributes.ReparsePoint))
            {
                try
                {
                    LocalPath localPath2 = localPath.Combine(item.FileName);
                    KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(localPath2.FullPath);
                    if (keyValuePair.Value)
                    {
                        localFileCache.StoreSymlink(localPath2, keyValuePair.Key);
                    }
                }
                catch
                {
                }
            }
            else if (item.HasAttribute(FileAttributes.Directory))
            {
                if (flag)
                {
                    CacheLocalFiles(localFileCache, localPath.Combine(item.FileName), flag);
                }
            }
            else
            {
                cachedDirectory.Register(1, item.FileName);
                localFileCache.StoreFileStamp(1, new LocalFileSystem.FileStamp(item));
            }
        }
    }
}
