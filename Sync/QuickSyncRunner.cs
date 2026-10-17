using SmarTTY.Ssh;
using SmarTTY.Ssh.QuickSync;
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
            foreach (SyncPlan.SymlinkRequest entry in syncPlan.Symlinks)
            {
                _writer.WriteLine("  " + entry.LinkPath + " => " + entry.Target + (entry.IsDirectory ? "(dir)" : "(file)"));
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

        public HelperBuilder(SshSession sshSession, string text, string outputPath)
        {
            _session = sshSession;
            this._sourcePath = text;
            this._outputPath = outputPath;
        }

        public void Install(Action<string> action)
        {
            string text = _session.GetTempPath("SysprogsSync.cpp");
            action?.Invoke("Sending " + this._sourcePath + " to " + text + "...");
            byte[] sourceBytes = File.ReadAllBytes(this._sourcePath);
            SshSession sshSession = _session;
            long fileLength = sourceBytes.Length;
            int? fileMode = Convert.ToInt32("666", 8);
            using (ScpChannelStream scpChannelStream = sshSession.OpenUpload(text, fileLength, default, fileMode))
            {
                scpChannelStream.Write(sourceBytes, 0, sourceBytes.Length);
            }
            BuildFlags buildFlags = BuildFlags.Default;
            BuildFlags previousBuildFlags;
            do
            {
                string command = "g++ " + text + " -O3 -o " + _outputPath;
                command = (((buildFlags & BuildFlags.UseRegexHeader) == 0) ? (command + " -std=c++11") : (command + " -DUSE_REGEX_H"));
                if ((buildFlags & BuildFlags.LargeFileOffset) != 0)
                {
                    command += " -D_FILE_OFFSET_BITS=64";
                }
                List<string> coll = _session.RunCommand(command, out var exitCode);
                previousBuildFlags = buildFlags;
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
                if (exitCode != 0)
                {
                    continue;
                }
                List<string> selftestOutput = _session.RunCommand(_outputPath + " --selftest", out exitCode);
                if (exitCode == 0)
                {
                    return;
                }
                foreach (string entry in selftestOutput.EnsureNotNull())
                {
                    action?.Invoke(entry);
                }
                buildFlags |= BuildFlags.UseRegexHeader;
            }
            while (buildFlags != previousBuildFlags);
            throw new Exception("Failed to build remote SysprogsSync executable. Please try building it manually.");
        }
    }

    private struct LocalDirectoryContext(LocalPath localPath)
    {
        public LocalPath Path = localPath;

        public bool Failed = false;

        public LocalDirectoryContext GetChild(string text)
        {
            LocalDirectoryContext child = new LocalDirectoryContext(Path.Combine(text));
            child.Failed = this.Failed;
            return child;
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
        TransferStatistics statistics;
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
                statistics = downloadPlanner.Statistics;
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
                this.ExecuteLocalActions(LocalPath.FromUserPath(syncSettings.LocalPath), syncPlan, remoteFileSource, syncEvents, ref statistics);
                CreateLinks(syncPlan, linkOptions, syncEvents);
            }
            else
            {
                if (syncSettings.Direction != SyncDirection.Upload)
                {
                    throw new ArgumentException("Direction");
                }
                SyncPlan.UploadPlanner uploadPlanner = new SyncPlan.UploadPlanner(remoteIsEmpty: remoteFileSet.Root.Files.Count <= 0 && remoteFileSet.Root.SubDirectories.Count <= 0 && remoteFileSet.Root.Symlinks.Count <= 0 && sshSession.RunCommandChecked("test -d \"" + syncSettings.RemotePath + "\"", requireSuccess: true) == null, syncSettings: syncSettings, remoteFileSet: remoteFileSet, localFileCache: localFileCache, syncCustomization: syncCustomization);
                uploadPlanner.Warning += (QuickSyncWarning quickSyncWarning, Exception exception) =>
                {
                    syncEvents?.OnWarning(new SyncWarning
                    {
                        Warning = quickSyncWarning,
                        Exception = exception
                    });
                };
                SyncPlan plan = uploadPlanner.BuildPlan();
                statistics = uploadPlanner.Statistics;
                (syncEvents as ISyncDiagnostics)?.OnPlanCreated(plan);
                syncEngine.Upload(plan.Actions, uploadPlanner.Files, ref statistics);
            }
        }
        finally
        {
            (syncEvents as LoggingSyncEvents)?.WriteWarnings();
        }
        return statistics;
    }

    private void ScanLocalDirectory(RemoteFileSet.RemoteDirectory remoteDirectory, LocalPath localPath, ISyncEvents syncEvents)
    {
        syncEvents?.OnProgress(new SyncProgress
        {
            Stage = SyncStage.BuildingFileList
        });
        foreach (LocalFileSystem.FindData entry in LocalFileSystem.EnumerateDirectory(localPath, ignoreMissing: false))
        {
            if (entry.HasAttribute(FileAttributes.ReparsePoint))
            {
                remoteDirectory.Symlinks.Add(new RemoteFileSet.RemoteDirectory.RemoteSymlink
                {
                    Name = entry.FileName,
                    IsDirectory = entry.HasAttribute(FileAttributes.Directory)
                });
            }
            else if (entry.HasAttribute(FileAttributes.Directory))
            {
                RemoteFileSet.RemoteDirectory item = new RemoteFileSet.RemoteDirectory(remoteDirectory, entry.FileName);
                try
                {
                    ScanLocalDirectory(item, localPath.Combine(entry.FileName), syncEvents);
                    remoteDirectory.SubDirectories.Add(item);
                }
                catch (Exception exception)
                {
                    syncEvents?.OnWarning(new SyncWarning
                    {
                        Exception = exception,
                        Warning = new QuickSyncWarning(QuickSyncWarningType.UnreadableLocalDirectory, localPath.ToString(), 0)
                    });
                }
            }
            else
            {
                LocalFileSystem.FileStamp fileStamp = new LocalFileSystem.FileStamp(entry);
                remoteDirectory.Files.Add(new RemoteFileSet.RemoteFileEntry
                {
                    Name = entry.FileName,
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
        for (int filterIndex = 0; filterIndex < items.Length; filterIndex++)
        {
            QuickSyncFilter quickSyncFilter = items[filterIndex];
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
        this.ExecuteLocalActions(localPath, syncPlan, null, syncEvents, ref transferStatistics, unused: true);
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
        bool disableHelper = (syncSettings.Options & SyncOptions.DisableHelper) == SyncOptions.DisableHelper;
        bool useTar = false;
        if (_engineKind == SyncEngineKind.Tar)
        {
            useTar = true;
        }
        else if (_engineKind == SyncEngineKind.Auto && (syncSettings.Options & SyncOptions.DisableTar) != SyncOptions.DisableTar && syncSettings.Direction == SyncDirection.Upload && sshSession.EnableOnTheFlyTar && ((syncSettings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.LocalCache && localFileCache != null) || syncSettings.QuickSyncOverwriteTrigger == QuickSyncOverwriteTrigger.Always))
        {
            useTar = true;
        }
        if (useTar)
        {
            return new TarSyncEngine(sshSession, syncSettings, syncEvents, localFileCache, Tuning, syncCustomization);
        }
        if (sshSession.Tag == null && !disableHelper)
        {
            string text = this._helperLocator?.FindHelper(sshSession.HostName);
            if (text != null && sshSession.RunCommandChecked("test -f \"" + text + "\"", requireSuccess: true) != null)
            {
                sshSession.Tag = text;
            }
            else
            {
                string helperName = "SysprogsSync-v1.2";
                if (sshSession.RunCommandChecked("which -f \"" + helperName + "\"", requireSuccess: true) != null || sshSession.RunCommandChecked("which \"" + helperName + "\"", requireSuccess: true) != null)
                {
                    sshSession.Tag = helperName;
                }
                else
                {
                    string homeDirectory = sshSession.RunCommandChecked("pwd", requireSuccess: true)?.FirstOrDefault((string path) => path.StartsWith("/"));
                    if (homeDirectory == null)
                    {
                        homeDirectory = RemotePathUtils.GetDirectoryName(sshSession.GetTempPath("test"));
                    }
                    text = homeDirectory + "/.SysprogsSync/" + helperName;
                    if (text != null && sshSession.RunCommandChecked("test -f \"" + text + "\"", requireSuccess: true) != null)
                    {
                        sshSession.Tag = text;
                    }
                    else
                    {
                        string helperDirectory = homeDirectory + "/.SysprogsSync";
                        sshSession.RunCommandChecked("mkdir -p \"" + helperDirectory + "\"", requireSuccess: false);
                        text = helperDirectory + "/SysprogsSync-v1.2";
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
        if ((sshSession.Tag == null) | disableHelper)
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
            catch (Exception exception)
            {
                syncEvents?.OnWarning(new SyncWarning
                {
                    Exception = exception,
                    Warning = new QuickSyncWarning(QuickSyncWarningType.CreateSymlinkFailed, item.LinkPath, 0)
                });
            }
        }
    }

    private static string GetRelativePath(LocalPath sourcePath, LocalPath targetPath)
    {
        if (targetPath.FullPath.StartsWith(sourcePath.FullPath, StringComparison.InvariantCultureIgnoreCase))
        {
            return targetPath.FullPath.Substring(sourcePath.FullPath.Length).TrimStart('\\').Replace('\\', '/');
        }
        return targetPath.FullPath;
    }

    private void ExecuteLocalActions(LocalPath localPath, SyncPlan syncPlan, IRemoteFileSource remoteFileSource, ISyncEvents syncEvents, ref TransferStatistics transferStatistics, bool unused = false)
    {
        Stack<LocalDirectoryContext> stack = new Stack<LocalDirectoryContext>();
        LocalDirectoryContext item = new LocalDirectoryContext(localPath);
        LocalFileSystem.CreateDirectoryRecursive(localPath);
        byte[] buffer = null;
        foreach (SyncPlan.SyncAction action in syncPlan.Actions)
        {
            switch (action.Type)
            {
                case SyncPlan.ActionType.CreateAndEnterDirectory:
                case SyncPlan.ActionType.EnterDirectory:
                    stack.Push(item);
                    item = item.GetChild(action.Path);
                    if (!item.Failed && action.Type == SyncPlan.ActionType.CreateAndEnterDirectory)
                    {
                        try
                        {
                            LocalFileSystem.CreateDirectoryRecursive(item.Path);
                        }
                        catch (Exception exception)
                        {
                            item.Failed = true;
                            syncEvents?.OnWarning(new SyncWarning
                            {
                                Exception = exception,
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
                        string text = GetRelativePath(localPath, item.Path.Combine(action.Path));
                        if (!item.Failed)
                        {
                            try
                            {
                                nativeFile = LocalFileSystem.OpenFile(item.Path.Combine(action.Path), LocalFileSystem.FileOpenMode.Create);
                            }
                            catch (Exception exception)
                            {
                                syncEvents?.OnWarning(new SyncWarning
                                {
                                    Exception = exception,
                                    Warning = new QuickSyncWarning
                                    {
                                        Type = QuickSyncWarningType.CannotCreateTargetFile,
                                        Path = text
                                    }
                                });
                            }
                        }
                        bool transferFailed = false;
                        using (IRemoteFileStream remoteFileStream = remoteFileSource.OpenFile(action.Argument))
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
                                    int bytesRead = remoteFileStream.ReadChunk(ref buffer);
                                    if (bytesRead == 0)
                                    {
                                        break;
                                    }
                                    transferStatistics.TransferredBytes += bytesRead;
                                    syncEvents?.OnProgress(new SyncProgress
                                    {
                                        Stage = SyncStage.Transferring,
                                        CurrentFile = text,
                                        Transferred = transferStatistics.TransferredBytes,
                                        Total = transferStatistics.TotalBytes
                                    });
                                    if (nativeFile == null || transferFailed)
                                    {
                                        continue;
                                    }
                                    try
                                    {
                                        if (nativeFile.Write(buffer, bytesRead) != bytesRead)
                                        {
                                            throw new IOException("Could not write all bytes to the file");
                                        }
                                    }
                                    catch (Exception exception)
                                    {
                                        transferFailed = true;
                                        syncEvents?.OnWarning(new SyncWarning
                                        {
                                            Exception = exception,
                                            Warning = new QuickSyncWarning
                                            {
                                                Type = QuickSyncWarningType.CannotUpdateLocalFile,
                                                Path = item.Path.Combine(action.Path).FullPath
                                            }
                                        });
                                    }
                                }
                                if (remoteFileStream.ErrorCode == 0 && !transferFailed && nativeFile != null)
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
                        LocalFileSystem.DeleteFile(LocalPath.FromRaw(action.Path), resetReadOnly: true);
                        transferStatistics.DeletedFiles++;
                        if (unused)
                        {
                            syncEvents?.OnProgress(new SyncProgress
                            {
                                Stage = SyncStage.DeletingOldContents,
                                Transferred = transferStatistics.DeletedFiles,
                                Total = transferStatistics.FilesToDelete
                            });
                        }
                    }
                    catch (Exception exception)
                    {
                        syncEvents?.OnWarning(new SyncWarning
                        {
                            Exception = exception,
                            Warning = new QuickSyncWarning
                            {
                                Type = QuickSyncWarningType.CannotDeleteTargetFile,
                                Path = item.Path.Combine(action.Path).FullPath
                            }
                        });
                    }
                    break;
                case SyncPlan.ActionType.DeleteEmptyTargetDirectory:
                    try
                    {
                        LocalFileSystem.DeleteDirectory(LocalPath.FromRaw(action.Path));
                    }
                    catch (Exception exception)
                    {
                        syncEvents?.OnWarning(new SyncWarning
                        {
                            Exception = exception,
                            Warning = new QuickSyncWarning
                            {
                                Type = QuickSyncWarningType.CannotRemoveTargetDirectory,
                                Path = item.Path.Combine(action.Path).FullPath
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

    public static void CacheLocalFiles(ILocalFileCache localFileCache, LocalPath localPath, bool recursive)
    {
        ICachedDirectory cachedDirectory = localFileCache.GetDirectory(localPath);
        foreach (LocalFileSystem.FindData item in LocalFileSystem.EnumerateDirectory(localPath, ignoreMissing: true))
        {
            if (item.HasAttribute(FileAttributes.ReparsePoint))
            {
                try
                {
                    LocalPath combinedPath = localPath.Combine(item.FileName);
                    KeyValuePair<string, bool> keyValuePair = ReparsePoints.ReadReparsePoint(combinedPath.FullPath);
                    if (keyValuePair.Value)
                    {
                        localFileCache.StoreSymlink(combinedPath, keyValuePair.Key);
                    }
                }
                catch
                {
                }
            }
            else if (item.HasAttribute(FileAttributes.Directory))
            {
                if (recursive)
                {
                    CacheLocalFiles(localFileCache, localPath.Combine(item.FileName), recursive);
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
